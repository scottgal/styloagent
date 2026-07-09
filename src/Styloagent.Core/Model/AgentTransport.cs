namespace Styloagent.Core.Model;

public enum TransportKind { Local, Ssh }

public sealed record AgentTransport(TransportKind Kind, string? SshHost = null, string? CredentialRef = null)
{
    public static readonly AgentTransport Local = new(TransportKind.Local);
}
