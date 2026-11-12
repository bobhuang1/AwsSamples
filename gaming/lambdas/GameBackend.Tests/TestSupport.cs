using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using NSubstitute;

namespace GameBackend.Tests;

internal static class TestSupport
{
    public static ILambdaContext Context()
    {
        var ctx = Substitute.For<ILambdaContext>();
        ctx.Logger.Returns(Substitute.For<ILambdaLogger>());
        return ctx;
    }

    public static APIGatewayHttpApiV2ProxyRequest Http(string method, string path, string? body = null, string? sub = null, bool iam = false,
        Dictionary<string, string>? query = null)
    {
        var request = new APIGatewayHttpApiV2ProxyRequest
        {
            RawPath = path,
            Body = body,
            QueryStringParameters = query,
            RequestContext = new APIGatewayHttpApiV2ProxyRequest.ProxyRequestContext
            {
                Stage = "$default",
                Http = new APIGatewayHttpApiV2ProxyRequest.HttpDescription { Method = method, Path = path },
            },
        };
        if (sub is not null)
        {
            request.RequestContext.Authorizer = new APIGatewayHttpApiV2ProxyRequest.AuthorizerDescription
            {
                Jwt = new APIGatewayHttpApiV2ProxyRequest.AuthorizerDescription.JwtDescription
                {
                    Claims = new Dictionary<string, string> { ["sub"] = sub },
                },
            };
        }
        if (iam)
        {
            request.RequestContext.Authorizer = new APIGatewayHttpApiV2ProxyRequest.AuthorizerDescription
            {
                IAM = new APIGatewayHttpApiV2ProxyRequest.AuthorizerDescription.IAMDescription { UserARN = "arn:aws:iam::123456789012:role/game-server" },
            };
        }
        return request;
    }

    public static AttributeValue S(string v) => new() { S = v };
    public static AttributeValue N(long v) => new() { N = v.ToString() };
}
