﻿using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Unleash.Internal;
using Unleash.Logging;
using Unleash.Metrics;
using Unleash.Serialization;

namespace Unleash.Communication
{
    internal class UnleashApiClient : IUnleashApiClient
    {
        private static readonly ILog Logger = LogProvider.GetLogger(typeof(UnleashApiClient));

        private readonly HttpClient httpClient;
        private readonly IJsonSerializer jsonSerializer;
        private readonly UnleashApiClientRequestHeaders clientRequestHeaders;

        public UnleashApiClient(
            HttpClient httpClient, 
            IJsonSerializer jsonSerializer, 
            UnleashApiClientRequestHeaders clientRequestHeaders)
        {
            this.httpClient = httpClient;
            this.jsonSerializer = jsonSerializer;
            this.clientRequestHeaders = clientRequestHeaders;
        }

        public async Task<FetchTogglesResult> FetchToggles(string etag, CancellationToken cancellationToken)
        {
            const string resourceUri = "api/client/features";

            using (var request = new HttpRequestMessage(HttpMethod.Get, resourceUri))
            {
                SetRequestHeaders(request, clientRequestHeaders);

                if (EntityTagHeaderValue.TryParse(etag, out var etagHeaderValue))
                    request.Headers.IfNoneMatch.Add(etagHeaderValue);

                using (var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false))
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        var error = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        Logger.Trace($"UNLEASH: Error {response.StatusCode} from server in '{nameof(FetchToggles)}': " + error);

                        return new FetchTogglesResult
                        {
                            HasChanged = false,
                            Etag = null,
                        };
                    }

                    var newEtag = response.Headers.ETag?.Tag;
                    if (newEtag == etag)
                    { 
                        return new FetchTogglesResult
                        {
                            HasChanged = false,
                            Etag = newEtag,
                            ToggleCollection = null,
                        };
                    }

                    var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                    var toggleCollection = jsonSerializer.Deserialize<ToggleCollection>(stream);

                    if (toggleCollection == null)
                    {
                        return new FetchTogglesResult
                        {
                            HasChanged = false
                        };
                    }

                    // Success
                    return new FetchTogglesResult
                    {
                        HasChanged = true,
                        Etag = newEtag,
                        ToggleCollection = toggleCollection
                    };
                }
            }
        }

        public async Task<bool> RegisterClient(ClientRegistration registration, CancellationToken cancellationToken)
        {
            const string requestUri = "api/client/register";

            var memoryStream = new MemoryStream();
            jsonSerializer.Serialize(memoryStream, registration);

            return await Post(requestUri, memoryStream, cancellationToken).ConfigureAwait(false);
        }

        public async Task<bool> SendMetrics(ThreadSafeMetricsBucket metrics, CancellationToken cancellationToken)
        {
            const string requestUri = "api/client/metrics";

            var memoryStream = new MemoryStream();

            using (metrics.StopCollectingMetrics(out var bucket))
            {
                jsonSerializer.Serialize(memoryStream, new ClientMetrics
                {
                    AppName = clientRequestHeaders.AppName,
                    InstanceId = clientRequestHeaders.InstanceTag,
                    Bucket = bucket
                });
            }

            return await Post(requestUri, memoryStream, cancellationToken).ConfigureAwait(false);
        }

        private async Task<bool> Post(string resourceUri, Stream stream, CancellationToken cancellationToken)
        {
            const int bufferSize = 1024 * 4;

            using (var request = new HttpRequestMessage(HttpMethod.Post, resourceUri))
            {
                request.Content = new StreamContent(stream, bufferSize);
                request.Content.Headers.AddContentTypeJson();

                SetRequestHeaders(request, clientRequestHeaders);

                using (var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false))
                {
                    if (response.IsSuccessStatusCode)
                        return true;

                    var error = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    Logger.Trace($"UNLEASH: Error {response.StatusCode} from request '{resourceUri}' in '{nameof(UnleashApiClient)}': " + error);

                    return false;
                }
            }
        }

        private static void SetRequestHeaders(HttpRequestMessage requestMessage, UnleashApiClientRequestHeaders headers)
        {
            const string appNameHeader = "UNLEASH-APPNAME";
            const string instanceIdHeader = "UNLEASH-INSTANCEID";

            requestMessage.Headers.TryAddWithoutValidation(appNameHeader, headers.AppName);
            requestMessage.Headers.TryAddWithoutValidation(instanceIdHeader, headers.InstanceTag);

            if (headers.CustomHttpHeaders == null)
                return;

            if (headers.CustomHttpHeaders.Count == 0)
                return;

            foreach (var header in headers.CustomHttpHeaders)
                requestMessage.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
    }
}
