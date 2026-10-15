using System.ServiceModel.Channels;
using System.ServiceModel.Description;
using System.ServiceModel.Dispatcher;

namespace Konta.EFactura;

/// <summary>What a caller may change about how an <see cref="EFacturaClient"/> reaches the service.</summary>
public sealed class EFacturaClientOptions
{
    /// <summary>How long one call may take; two minutes when not given.</summary>
    public TimeSpan? Timeout { get; init; }

    /// <summary>
    /// A proxy, for example <c>socks5://127.0.0.1:1080</c>, to reach the test service from an
    /// address SFS has not registered.
    /// </summary>
    public Uri? Proxy { get; init; }

    /// <summary>
    /// Handed each SOAP message as it leaves (<c>"request"</c>) and as the answer arrives
    /// (<c>"response"</c>), to keep request and response fixtures. The request is seen before the
    /// security header is added, so the password is never in it.
    /// </summary>
    public Action<string, string>? Recorder { get; init; }

    /// <summary>
    /// Wraps or replaces the HTTP handler the client sends through: given the handler WCF built,
    /// return the one to use. A test can answer from recorded fixtures this way without a server,
    /// keeping the real binding, serialiser and security header.
    /// </summary>
    public Func<HttpClientHandler, HttpMessageHandler>? Transport { get; init; }
}

/// <summary>Puts <see cref="EFacturaClientOptions.Transport"/> where WCF's HTTP channel looks for it.</summary>
internal sealed class TransportBehavior(Func<HttpClientHandler, HttpMessageHandler> transport) : IEndpointBehavior
{
    public void AddBindingParameters(ServiceEndpoint endpoint, BindingParameterCollection bindingParameters)
    {
        ArgumentNullException.ThrowIfNull(bindingParameters);

        bindingParameters.Add(transport);
    }

    public void ApplyClientBehavior(ServiceEndpoint endpoint, ClientRuntime clientRuntime)
    {
    }

    public void ApplyDispatchBehavior(ServiceEndpoint endpoint, EndpointDispatcher endpointDispatcher)
    {
    }

    public void Validate(ServiceEndpoint endpoint)
    {
    }
}
