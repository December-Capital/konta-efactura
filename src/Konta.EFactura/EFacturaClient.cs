using System.ServiceModel;
using Konta.EFactura.Generated;

namespace Konta.EFactura;

/// <summary>Which SIA "e-Factura" service a client talks to.</summary>
public enum EFacturaEnvironment
{
    /// <summary>SFS's test service. It answers only client addresses SFS has registered.</summary>
    Test,

    /// <summary>The real service.</summary>
    Production,
}

/// <summary>The service addresses, from SFS's integration package (<c>spec/sfs/README.md</c>).</summary>
public static class EFacturaEndpoints
{
    /// <summary>The test service.</summary>
    public static readonly Uri Test = new("https://apiefactura-pre.sfs.md/Service.svc");

    /// <summary>The real service.</summary>
    public static readonly Uri Production = new("https://efactura-api.sfs.md/Service.svc");

    /// <summary>The address of an environment.</summary>
    /// <param name="environment">Test or production.</param>
    /// <returns>Its service address.</returns>
    public static Uri For(EFacturaEnvironment environment) =>
        environment switch
        {
            EFacturaEnvironment.Test => Test,
            EFacturaEnvironment.Production => Production,
            _ => throw new ArgumentOutOfRangeException(nameof(environment), environment, "Unknown e-Factura environment."),
        };
}

/// <summary>What the service answered to <see cref="EFacturaClient.TestAsync"/>.</summary>
/// <param name="RequestId">The service's id for the request.</param>
/// <param name="Status">The service's status code, as it sent it. Its meanings are not documented yet.</param>
/// <param name="TimeStamp">When the service answered, by its own clock.</param>
public sealed record EFacturaTestResult(string? RequestId, int Status, DateTime TimeStamp);

/// <summary>
/// A connection to SIA "e-Factura" for one legal entity's API user. The contract is generated from
/// <c>spec/sfs/Service.wsdl</c> into <c>Generated/</c> and kept internal: callers get typed methods
/// here as each operation is confirmed against the test service, never the wire shapes.
/// </summary>
/// <remarks>
/// The API user is created in e-Factura by the company's manager, and can do everything the platform
/// allows for that company. Keep its password out of source code, configuration files under version
/// control and logs.
/// </remarks>
public sealed class EFacturaClient : IAsyncDisposable
{
    /// <summary>The largest response accepted: invoice batches with attachments are large.</summary>
    public const int MaxMessageBytes = 32 * 1024 * 1024;

    private readonly ServiceClient _client;

    /// <summary>Opens nothing yet: the first call connects.</summary>
    /// <param name="environment">Test or production.</param>
    /// <param name="userName">The company's API user.</param>
    /// <param name="password">Its password.</param>
    /// <param name="timeout">How long one call may take; two minutes when not given.</param>
    public EFacturaClient(EFacturaEnvironment environment, string userName, string password, TimeSpan? timeout = null)
        : this(EFacturaEndpoints.For(environment), userName, password, timeout)
    {
    }

    /// <summary>For an address other than the two published ones, such as a local stand-in in tests.</summary>
    /// <param name="address">The service address.</param>
    /// <param name="userName">The company's API user.</param>
    /// <param name="password">Its password.</param>
    /// <param name="timeout">How long one call may take; two minutes when not given.</param>
    public EFacturaClient(Uri address, string userName, string password, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(address);
        ArgumentException.ThrowIfNullOrWhiteSpace(userName);
        ArgumentException.ThrowIfNullOrEmpty(password);

        _client = new ServiceClient(Binding(timeout ?? TimeSpan.FromMinutes(2)), new EndpointAddress(address));
        _client.ClientCredentials.UserName.UserName = userName;
        _client.ClientCredentials.UserName.Password = password;
    }

    /// <summary>The address this client calls.</summary>
    public Uri Address => _client.Endpoint.Address.Uri;

    /// <summary>
    /// The service's <c>Test</c> operation: proves the address is reachable from here and the API
    /// user's credentials are accepted, without touching any invoice.
    /// </summary>
    /// <param name="message">Anything; the service is expected to answer it.</param>
    /// <param name="cancellationToken">Cancellation. The call itself may still finish on the server.</param>
    /// <returns>The service's answer.</returns>
    public async Task<EFacturaTestResult> TestAsync(string message, CancellationToken cancellationToken = default)
    {
        var response = await _client.TestAsync(message).WaitAsync(cancellationToken).ConfigureAwait(false);

        return new EFacturaTestResult(response.RequestId, response.Status, response.TimeStamp);
    }

    /// <summary>
    /// The binding SFS's guide describes: <c>basicHttpBinding</c> with
    /// <c>TransportWithMessageCredential</c>, so HTTPS carries the call and the user name and
    /// password travel in the SOAP header of every message.
    /// </summary>
    /// <param name="timeout">How long one call may take.</param>
    internal static BasicHttpBinding Binding(TimeSpan timeout)
    {
        var binding = new BasicHttpBinding(BasicHttpSecurityMode.TransportWithMessageCredential)
        {
            MaxReceivedMessageSize = MaxMessageBytes,
            MaxBufferSize = MaxMessageBytes,
            OpenTimeout = timeout,
            SendTimeout = timeout,
            ReceiveTimeout = timeout,
            CloseTimeout = TimeSpan.FromSeconds(30),
        };

        binding.Security.Message.ClientCredentialType = BasicHttpMessageCredentialType.UserName;
        binding.ReaderQuotas.MaxStringContentLength = MaxMessageBytes;
        binding.ReaderQuotas.MaxArrayLength = MaxMessageBytes;

        return binding;
    }

    /// <summary>Closes the connection, or aborts it when it has faulted.</summary>
    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_client.State == CommunicationState.Faulted)
            {
                _client.Abort();
            }
            else
            {
                await _client.CloseAsync().ConfigureAwait(false);
            }
        }
        catch (CommunicationException)
        {
            _client.Abort();
        }
        catch (TimeoutException)
        {
            _client.Abort();
        }
    }
}
