﻿using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Sustainsys.Saml2.Bindings;
using Sustainsys.Saml2.Xml;
using Sustainsys.Saml2.Saml;
using Sustainsys.Saml2.Samlp;
using System.Net.Http;
using System.Text.Encodings.Web;
using System.Xml;
using Sustainsys.Saml2.Tests.Helpers;
using System.Text;
using Sustainsys.Saml2.Serialization;

namespace Sustainsys.Saml2.AspNetCore.Tests;
public class Saml2HandlerTests
{
    private readonly static DateTime CurrentFakeTime = new(2023, 09, 08, 14, 53, 02, DateTimeKind.Utc);

    private static async Task<(Saml2Handler subject, HttpContext httpContext)> CreateSubject(Saml2Options options)
    {
        var optionsMonitor = Substitute.For<IOptionsMonitor<Saml2Options>>();
        optionsMonitor.Get(Arg.Any<string>()).Returns(options);

        var loggerFactory = Substitute.For<ILoggerFactory>();

#if !NET8_0_OR_GREATER
        var systemClock = Substitute.For<ISystemClock>();
        systemClock.UtcNow.Returns(CurrentFakeTime);
#endif

        var handler = new Saml2Handler(
            optionsMonitor,
            loggerFactory,
            UrlEncoder.Default
#if NET8_0_OR_GREATER
            );
#else
            ,systemClock);
#endif

        var scheme = new AuthenticationScheme("Saml2", "Saml2", typeof(Saml2Handler));

        var httpContext = Substitute.For<HttpContext>();
        httpContext.Request.Returns(Substitute.For<HttpRequest>());
        httpContext.Request.Scheme = "https";
        httpContext.Request.Host = new HostString("sp.example.com", 8888);
        httpContext.Request.Path = "/path";

        await handler.InitializeAsync(scheme, httpContext);

        return (handler, httpContext);
    }

    private static Saml2Options CreateOptions()
    {
        return new Saml2Options()
        {
            EntityId = "https://sp.example.com/Metadata",
            IdentityProvider = new()
            {
                EntityId = "https://idp.example.com",
                SsoServiceUrl = "https://idp.example.com/sso",
                SsoServiceBinding = Constants.BindingUris.HttpRedirect
            },
#if NET8_0_OR_GREATER
            TimeProvider = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(CurrentFakeTime)
#endif
        };
    }

    [Fact]
    public async Task ChallengeCreatesAuthnRequest()
    {
        var options = CreateOptions();

        bool eventCalled = false;

        options.Events = new()
        {
            // Use event to validate contents of AuthnRequest
            OnAuthnRequestGeneratedAsync = ctx =>
            {
                // Profile 4.1.4.1: Issuer is required for WebSSO
                ctx.AuthnRequest.Issuer?.Value.Should().Be("https://sp.example.com/Metadata");

                // Core 3.2.1: Issue instant is required
                ctx.AuthnRequest.IssueInstant.Should().Be(CurrentFakeTime);

                // Core 3.4.1 AssertionConsumerServicerUrl is optional, but our StubIdp requires it
                ctx.AuthnRequest.AssertionConsumerServiceUrl.Should().Be("https://sp.example.com:8888/Saml2/Acs");

                eventCalled = true;
                return Task.CompletedTask;
            }
        };

        (var subject, var httpContext) = await CreateSubject(options);

        var props = new AuthenticationProperties();

        await subject.ChallengeAsync(props);

        eventCalled.Should().BeTrue("The OnAuthnRequestGeneratedAsync event should have been called");
    }

    [Fact]
    public async Task ChallengeSetsRedirect()
    {
        // This test only validates that the AuthnRequest ends up as a redirect. The contents of
        // the AuthnRequest are validated in ChallengeCreatesAuthnRequest through the event.

        var options = CreateOptions();

        AuthnRequest? authnRequest = null;

        options.Events = new()
        {
            OnAuthnRequestGeneratedAsync = ctx =>
            {
                authnRequest = ctx.AuthnRequest;
                return Task.CompletedTask;
            }
        };

        (var subject, var httpContext) = await CreateSubject(options);

        var props = new AuthenticationProperties();

        await subject.ChallengeAsync(props);

        //bool validated = false;

        void validateLocation(string location)
        {
            location.Should().StartWith("https://idp.example.com/sso?SamlRequest=");

            var message = new HttpRedirectBinding().UnBindAsync(location, _ => throw new NotImplementedException());

            var deserializedAuthnRequest = new SamlXmlReader()
                .ReadAuthnRequest(message.Xml.GetXmlTraverser());

            deserializedAuthnRequest.Should().BeEquivalentTo(authnRequest);

            // validated = true;
        }

        httpContext.Response.Received().Redirect(Arg.Do<string>(validateLocation));

        // TODO: Check that validation was called.
        //validated.Should().BeTrue();
    }


    [Fact]
    public async Task HandleRemoteAuthenticate_CannotUnbind()
    {
        var options = CreateOptions();
        var (subject, _) = await CreateSubject(options);

        var result = await subject.HandleRequestAsync();

        result.Should().BeFalse();
    }


    [Fact]
    // Full happy path test case for a signed response via Http POST binding
    public async Task HandleRemoteAuthenticate()
    {
        var options = CreateOptions();
        var (subject, httpContext) = await CreateSubject(options);

        // Have to match callback path.
        httpContext.Request.Path = "/Saml2/Acs";
        httpContext.Request.Method = "POST";

        var xmlDoc = TestData.GetXmlDocument<Saml2HandlerTests>();

        var encodedResponse = Convert.ToBase64String(Encoding.UTF8.GetBytes(xmlDoc.OuterXml));

        httpContext.Request.Form = new FormCollection(new()
        {
            { "SAMLResponse", new(encodedResponse) }
        });

        var result = await subject.HandleRequestAsync();

        result.Should().BeTrue();
    }

    // TODO: Use event to resolve IdentityProvider - presence of EntityId indicates if challenge or response processing
    // TODO: Event when Xml was created
}