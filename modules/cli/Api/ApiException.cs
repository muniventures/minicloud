// SPDX-License-Identifier: MPL-2.0

namespace Minicloud.Cli.Api;

public sealed class ApiException : Exception
{
    public ApiException(int statusCode, string code, string message)
        : base(message)
    {
        StatusCode = statusCode;
        Code = code;
    }

    public int StatusCode { get; }
    public string Code { get; }
}
