using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace ChatRelay;

/// <summary>
/// Lambda REQUEST authorizer for the WebSocket $connect route. Browsers cannot set an
/// Authorization header on a WebSocket handshake, so the client passes its Cognito ID
/// token as <c>?token=</c>. The token is validated against the user pool's signing keys
/// (issuer, audience = app client id, lifetime) and the caller's identity is handed to
/// the $connect handler through the authorizer context, so the handler never trusts a
/// user id from the query string.
/// </summary>
public sealed class ConnectAuthorizer
{
    private static readonly string Issuer = Environment.GetEnvironmentVariable("JWT_ISSUER") ?? "";
    private static readonly string Audience = Environment.GetEnvironmentVariable("JWT_AUDIENCE") ?? "";

    private static readonly ConfigurationManager<OpenIdConnectConfiguration> Oidc = new(
        $"{Issuer}/.well-known/openid-configuration",
        new OpenIdConnectConfigurationRetriever(),
        new HttpDocumentRetriever { RequireHttps = true });

    private static readonly JsonWebTokenHandler Tokens = new();

    public async Task<APIGatewayCustomAuthorizerResponse> Handle(APIGatewayCustomAuthorizerRequest request, ILambdaContext context)
    {
        string? token = null;
        request.QueryStringParameters?.TryGetValue("token", out token);
        if (string.IsNullOrEmpty(token))
            throw new UnauthorizedAccessException("Unauthorized"); // API Gateway answers 401

        var config = await Oidc.GetConfigurationAsync(CancellationToken.None);
        var result = await Tokens.ValidateTokenAsync(token, new TokenValidationParameters
        {
            ValidIssuer = Issuer,
            ValidAudience = Audience,
            IssuerSigningKeys = config.SigningKeys,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromMinutes(1),
        });

        if (!result.IsValid
            || !result.Claims.TryGetValue("token_use", out var use) || use as string != "id"
            || !result.Claims.TryGetValue("sub", out var sub) || sub is not string userId || userId.Length == 0)
        {
            context.Logger.LogInformation("Rejected WebSocket token: {0}", result.Exception?.Message ?? "wrong token type or no sub");
            return Policy("anonymous", "Deny", request.MethodArn, null);
        }

        var userName = result.Claims.TryGetValue("cognito:username", out var u) && u is string s && s.Length > 0 ? s : userId;
        return Policy(userId, "Allow", request.MethodArn, new APIGatewayCustomAuthorizerContextOutput
        {
            ["userId"] = userId,
            ["userName"] = userName,
        });
    }

    private static APIGatewayCustomAuthorizerResponse Policy(string principal, string effect, string resource, APIGatewayCustomAuthorizerContextOutput? ctx) => new()
    {
        PrincipalID = principal,
        PolicyDocument = new APIGatewayCustomAuthorizerPolicy
        {
            Version = "2012-10-17",
            Statement =
            [
                new APIGatewayCustomAuthorizerPolicy.IAMPolicyStatement
                {
                    Action = ["execute-api:Invoke"],
                    Effect = effect,
                    Resource = [resource],
                },
            ],
        },
        Context = ctx,
    };

    /// <summary>Reads a value the authorizer put in the request context ($connect only).</summary>
    public static string? FromContext(APIGatewayProxyRequest request, string key)
        => request.RequestContext?.Authorizer is { } a && a.TryGetValue(key, out var v) ? v?.ToString() : null;
}
