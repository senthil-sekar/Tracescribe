using Azure.Core;
using Azure.Identity;
using Tracescribe.Query;

namespace Tracescribe.Mcp;

/// <summary>
/// Bearer tokens for the Log Analytics query API from <see cref="DefaultAzureCredential"/> (Azure CLI login, managed
/// identity, workload identity, environment service principal, ...), cached until shortly before they expire.
/// </summary>
public sealed class AzureTokenProvider
{
    private readonly TokenCredential _credential;
    private readonly TokenRequestContext _context;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private AccessToken _token;

    /// <summary>Creates the provider.</summary>
    /// <param name="credential">Credential to use; defaults to <see cref="DefaultAzureCredential"/>.</param>
    /// <param name="scope">Token scope; defaults to <c>TRACESCRIBE_AZURE_SCOPE</c> or <see cref="AzureMonitorBackend.DefaultScope"/>.</param>
    public AzureTokenProvider(TokenCredential? credential = null, string? scope = null)
    {
        _credential = credential ?? new DefaultAzureCredential();
        _context = new TokenRequestContext([scope ?? Environment.GetEnvironmentVariable("TRACESCRIBE_AZURE_SCOPE") ?? AzureMonitorBackend.DefaultScope]);
    }

    /// <summary>Returns a token valid for at least five more minutes.</summary>
    public async ValueTask<string> GetTokenAsync(CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_token.ExpiresOn - DateTimeOffset.UtcNow < TimeSpan.FromMinutes(5))
            {
                try
                {
                    _token = await _credential.GetTokenAsync(_context, cancellationToken).ConfigureAwait(false);
                }
                catch (AuthenticationFailedException ex)
                {
                    throw new TraceBackendException("azuremonitor: could not get an Azure token. Run 'az login' or configure a managed identity. " + ex.Message, ex);
                }
            }

            return _token.Token;
        }
        finally
        {
            _lock.Release();
        }
    }
}
