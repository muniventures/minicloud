namespace Minicloud.Common.Contracts;

public sealed record DatabaseBindingRequest(string AppId, IReadOnlyList<string> ServiceNames);
