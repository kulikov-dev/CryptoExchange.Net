using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using CryptoExchange.Net.Interfaces;
using CryptoExchange.Net.Logging;
using CryptoExchange.Net.Objects;
using CryptoExchange.Net.Requests;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CryptoExchange.Net
{
    /// <summary>
    /// Base rest API client for interacting with a REST API
    /// </summary>
    public abstract class RestApiClient : BaseApiClient, IRestApiClient
    {
        /// <inheritdoc />
        public IRequestFactory RequestFactory { get; set; } = new RequestFactory();
        /// <inheritdoc />
        public abstract TimeSyncInfo? GetTimeSyncInfo();

        /// <inheritdoc />
        public abstract TimeSpan? GetTimeOffset();

        /// <inheritdoc />
        public int TotalRequestsMade { get; set; }

        /// <summary>
        /// Request headers to be sent with each request
        /// </summary>
        protected Dictionary<string, string>? StandardRequestHeaders { get; set; }

        /// <summary>
        /// Options for this client
        /// </summary>
        public new RestApiClientOptions Options => (RestApiClientOptions)base.Options;

        /// <summary>
        /// List of rate limiters
        /// </summary>
        internal IEnumerable<IRateLimiter> RateLimiters { get; }

        /// <summary>
        /// Options
        /// </summary>
        internal ClientOptions ClientOptions { get; set; }

        /// <summary>
        /// ctor
        /// </summary>
        /// <param name="log">Logger</param>
        /// <param name="options">The base client options</param>
        /// <param name="apiOptions">The Api client options</param>
        public RestApiClient(Log log, ClientOptions options, RestApiClientOptions apiOptions) : base(log, options, apiOptions)
        {
            var rateLimiters = new List<IRateLimiter>();
            foreach (var rateLimiter in apiOptions.RateLimiters)
                rateLimiters.Add(rateLimiter);
            RateLimiters = rateLimiters;
            ClientOptions = options;

            RequestFactory.Configure(apiOptions.RequestTimeout, options.Proxy, apiOptions.HttpClient);
        }

        /// <summary>
        /// Execute a request to the uri and returns if it was successful
        /// </summary>
        /// <param name="uri">The uri to send the request to</param>
        /// <param name="method">The method of the request</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <param name="parameters">The parameters of the request</param>
        /// <param name="signed">Whether or not the request should be authenticated</param>
        /// <param name="parameterPosition">Where the parameters should be placed, overwrites the value set in the client</param>
        /// <param name="arraySerialization">How array parameters should be serialized, overwrites the value set in the client</param>
        /// <param name="requestWeight">Credits used for the request</param>
        /// <param name="deserializer">The JsonSerializer to use for deserialization</param>
        /// <param name="additionalHeaders">Additional headers to send with the request</param>
        /// <param name="ignoreRatelimit">Ignore rate limits for this request</param>
        /// <returns></returns>
        [return: NotNull]
        protected virtual async Task<WebCallResult> SendRequestAsync(
            Uri uri,
            HttpMethod method,
            CancellationToken cancellationToken,
            Dictionary<string, object>? parameters = null,
            bool signed = false,
            HttpMethodParameterPosition? parameterPosition = null,
            ArrayParametersSerialization? arraySerialization = null,
            int requestWeight = 1,
            JsonSerializer? deserializer = null,
            Dictionary<string, string>? additionalHeaders = null,
            bool ignoreRatelimit = false)
        {
            int currentTry = 0;
            while (true)
            {
                currentTry++;
                var request = await PrepareRequestAsync(uri, method, cancellationToken, parameters, signed, parameterPosition, arraySerialization, requestWeight, deserializer, additionalHeaders, ignoreRatelimit).ConfigureAwait(false);
                if (!request)
                    return new WebCallResult(request.Error!);

                var result = await GetResponseAsync<object>(request.Data, deserializer, cancellationToken, true).ConfigureAwait(false);
                if (await ShouldRetryRequestAsync(result, currentTry).ConfigureAwait(false))
                    continue;

                return result.AsDataless();
            }
        }

        /// <summary>
        /// Execute a request to the uri and deserialize the response into the provided type parameter
        /// </summary>
        /// <typeparam name="T">The type to deserialize into</typeparam>
        /// <param name="uri">The uri to send the request to</param>
        /// <param name="method">The method of the request</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <param name="parameters">The parameters of the request</param>
        /// <param name="signed">Whether or not the request should be authenticated</param>
        /// <param name="parameterPosition">Where the parameters should be placed, overwrites the value set in the client</param>
        /// <param name="arraySerialization">How array parameters should be serialized, overwrites the value set in the client</param>
        /// <param name="requestWeight">Credits used for the request</param>
        /// <param name="deserializer">The JsonSerializer to use for deserialization</param>
        /// <param name="additionalHeaders">Additional headers to send with the request</param>
        /// <param name="ignoreRatelimit">Ignore rate limits for this request</param>
        /// <returns></returns>
        [return: NotNull]
        protected virtual async Task<WebCallResult<T>> SendRequestAsync<T>(
            Uri uri,
            HttpMethod method,
            CancellationToken cancellationToken,
            Dictionary<string, object>? parameters = null,
            bool signed = false,
            HttpMethodParameterPosition? parameterPosition = null,
            ArrayParametersSerialization? arraySerialization = null,
            int requestWeight = 1,
            JsonSerializer? deserializer = null,
            Dictionary<string, string>? additionalHeaders = null,
            bool ignoreRatelimit = false
            ) where T : class
        {
            int currentTry = 0;
            while (true)
            {
                currentTry++;
                var request = await PrepareRequestAsync(uri, method, cancellationToken, parameters, signed, parameterPosition, arraySerialization, requestWeight, deserializer, additionalHeaders, ignoreRatelimit).ConfigureAwait(false);
                if (!request)
                    return new WebCallResult<T>(request.Error!);

                var result = await GetResponseAsync<T>(request.Data, deserializer, cancellationToken, false).ConfigureAwait(false);
                if (await ShouldRetryRequestAsync(result, currentTry).ConfigureAwait(false))
                    continue;

                return result;
            }
        }

        /// <summary>
        /// Prepares a request to be sent to the server
        /// </summary>
        /// <param name="uri">The uri to send the request to</param>
        /// <param name="method">The method of the request</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <param name="parameters">The parameters of the request</param>
        /// <param name="signed">Whether or not the request should be authenticated</param>
        /// <param name="parameterPosition">Where the parameters should be placed, overwrites the value set in the client</param>
        /// <param name="arraySerialization">How array parameters should be serialized, overwrites the value set in the client</param>
        /// <param name="requestWeight">Credits used for the request</param>
        /// <param name="deserializer">The JsonSerializer to use for deserialization</param>
        /// <param name="additionalHeaders">Additional headers to send with the request</param>
        /// <param name="ignoreRatelimit">Ignore rate limits for this request</param>
        /// <returns></returns>
        protected virtual async Task<CallResult<IRequest>> PrepareRequestAsync(
            Uri uri,
            HttpMethod method,
            CancellationToken cancellationToken,
            Dictionary<string, object>? parameters = null,
            bool signed = false,
            HttpMethodParameterPosition? parameterPosition = null,
            ArrayParametersSerialization? arraySerialization = null,
            int requestWeight = 1,
            JsonSerializer? deserializer = null,
            Dictionary<string, string>? additionalHeaders = null,
            bool ignoreRatelimit = false)
        {
            var requestId = NextId();

            if (signed)
            {
                var syncTask = SyncTimeAsync();
                var timeSyncInfo = GetTimeSyncInfo();

                if (timeSyncInfo != null && timeSyncInfo.TimeSyncState.LastSyncTime == default)
                {
                    // Initially with first request we'll need to wait for the time syncing, if it's not the first request we can just continue
                    var syncTimeResult = await syncTask.ConfigureAwait(false);
                    if (!syncTimeResult)
                    {
                        _log.Write(LogLevel.Debug, $"[{requestId}] Failed to sync time, aborting request: " + syncTimeResult.Error);
                        return syncTimeResult.As<IRequest>(default);
                    }
                }
            }

            if (!ignoreRatelimit)
            {
                foreach (var limiter in RateLimiters)
                {
                    var limitResult = await limiter.LimitRequestAsync(_log, uri.AbsolutePath, method, signed, Options.ApiCredentials?.Key, Options.RateLimitingBehaviour, requestWeight, cancellationToken).ConfigureAwait(false);
                    if (!limitResult.Success)
                        return new CallResult<IRequest>(limitResult.Error!);
                }
            }

            if (signed && AuthenticationProvider == null)
            {
                _log.Write(LogLevel.Warning, $"[{requestId}] Request {uri.AbsolutePath} failed because no ApiCredentials were provided");
                return new CallResult<IRequest>(new NoApiCredentialsError());
            }

            _log.Write(LogLevel.Information, $"[{requestId}] Creating request for " + uri);
            var paramsPosition = parameterPosition ?? ParameterPositions[method];
            var request = ConstructRequest(uri, method, parameters?.OrderBy(p => p.Key).ToDictionary(p => p.Key, p => p.Value), signed, paramsPosition, arraySerialization ?? this.arraySerialization, requestId, additionalHeaders);

            string? paramString = "";
            if (paramsPosition == HttpMethodParameterPosition.InBody)
                paramString = $" with request body '{request.Content}'";

            var headers = request.GetHeaders();
            if (headers.Any())
                paramString += " with headers " + string.Join(", ", headers.Select(h => h.Key + $"=[{string.Join(",", h.Value)}]"));

            TotalRequestsMade++;
            _log.Write(LogLevel.Trace, $"[{requestId}] Sending {method}{(signed ? " signed" : "")} request to {request.Uri}{paramString ?? " "}{(ClientOptions.Proxy == null ? "" : $" via proxy {ClientOptions.Proxy.Host}")}");
            return new CallResult<IRequest>(request);
        }

        /// <summary>
        /// Executes the request and returns the result deserialized into the type parameter class
        /// </summary>
        /// <param name="request">The request object to execute</param>
        /// <param name="deserializer">The JsonSerializer to use for deserialization</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <param name="expectedEmptyResponse">If an empty response is expected</param>
        /// <returns></returns>
        protected virtual async Task<WebCallResult<T>> GetResponseAsync<T>(
            IRequest request,
            JsonSerializer? deserializer,
            CancellationToken cancellationToken,
            bool expectedEmptyResponse)
        {
            try
            {
                var sw = Stopwatch.StartNew();
                var response = await request.GetResponseAsync(cancellationToken).ConfigureAwait(false);
                sw.Stop();
                var statusCode = response.StatusCode;
                var headers = response.ResponseHeaders;
                var responseStream = await response.GetResponseStreamAsync().ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    // If we have to manually parse error responses (can't rely on HttpStatusCode) we'll need to read the full
                    // response before being able to deserialize it into the resulting type since we don't know if it an error response or data
                    if (manualParseError)
                    {
                        using var reader = new StreamReader(responseStream);
                        var data = await reader.ReadToEndAsync().ConfigureAwait(false);
                        responseStream.Close();
                        response.Close();
                        _log.Write(LogLevel.Debug, $"[{request.RequestId}] Response received in {sw.ElapsedMilliseconds}ms{(_log.Level == LogLevel.Trace ? (": " + data) : "")}");

                        if (!expectedEmptyResponse)
                        {
                            // Validate if it is valid json. Sometimes other data will be returned, 502 error html pages for example
                            var parseResult = ValidateJson(data);
                            if (!parseResult.Success)
                                return new WebCallResult<T>(response.StatusCode, response.ResponseHeaders, sw.Elapsed, Options.OutputOriginalData ? data : null, request.Uri.ToString(), request.Content, request.Method, request.GetHeaders(), default, parseResult.Error!);

                            // Let the library implementation see if it is an error response, and if so parse the error
                            var error = await TryParseErrorAsync(parseResult.Data).ConfigureAwait(false);
                            if (error != null)
                                return new WebCallResult<T>(response.StatusCode, response.ResponseHeaders, sw.Elapsed, Options.OutputOriginalData ? data : null, request.Uri.ToString(), request.Content, request.Method, request.GetHeaders(), default, error!);

                            // Not an error, so continue deserializing
                            var deserializeResult = Deserialize<T>(parseResult.Data, deserializer, request.RequestId);
                            return new WebCallResult<T>(response.StatusCode, response.ResponseHeaders, sw.Elapsed, Options.OutputOriginalData ? data : null, request.Uri.ToString(), request.Content, request.Method, request.GetHeaders(), deserializeResult.Data, deserializeResult.Error);
                        }
                        else
                        {
                            if (!string.IsNullOrEmpty(data))
                            {
                                var parseResult = ValidateJson(data);
                                if (!parseResult.Success)
                                    // Not empty, and not json
                                    return new WebCallResult<T>(response.StatusCode, response.ResponseHeaders, sw.Elapsed, Options.OutputOriginalData ? data : null, request.Uri.ToString(), request.Content, request.Method, request.GetHeaders(), default, parseResult.Error!);

                                var error = await TryParseErrorAsync(parseResult.Data).ConfigureAwait(false);
                                if (error != null)
                                    // Error response
                                    return new WebCallResult<T>(response.StatusCode, response.ResponseHeaders, sw.Elapsed, Options.OutputOriginalData ? data : null, request.Uri.ToString(), request.Content, request.Method, request.GetHeaders(), default, error!);
                            }

                            // Empty success response; okay
                            return new WebCallResult<T>(response.StatusCode, response.ResponseHeaders, sw.Elapsed, Options.OutputOriginalData ? data : null, request.Uri.ToString(), request.Content, request.Method, request.GetHeaders(), default, default);
                        }
                    }
                    else
                    {
                        if (expectedEmptyResponse)
                        {
                            // We expected an empty response and the request is successful and don't manually parse errors, so assume it's correct
                            responseStream.Close();
                            response.Close();

                            return new WebCallResult<T>(statusCode, headers, sw.Elapsed, null, request.Uri.ToString(), request.Content, request.Method, request.GetHeaders(), default, null);
                        }

                        // Success status code, and we don't have to check for errors. Continue deserializing directly from the stream
                        var desResult = await DeserializeAsync<T>(responseStream, deserializer, request.RequestId, sw.ElapsedMilliseconds).ConfigureAwait(false);
                        responseStream.Close();
                        response.Close();

                        return new WebCallResult<T>(statusCode, headers, sw.Elapsed, Options.OutputOriginalData ? desResult.OriginalData : null, request.Uri.ToString(), request.Content, request.Method, request.GetHeaders(), desResult.Data, desResult.Error);
                    }
                }
                else
                {
                    // Http status code indicates error
                    using var reader = new StreamReader(responseStream);
                    var data = await reader.ReadToEndAsync().ConfigureAwait(false);
                    _log.Write(LogLevel.Warning, $"[{request.RequestId}] Error received in {sw.ElapsedMilliseconds}ms: {data}");
                    responseStream.Close();
                    response.Close();
                    var parseResult = ValidateJson(data);
                    var error = parseResult.Success ? ParseErrorResponse(parseResult.Data) : new ServerError(data)!;
                    if (error.Code == null || error.Code == 0)
                        error.Code = (int)response.StatusCode;
                    return new WebCallResult<T>(statusCode, headers, sw.Elapsed, data, request.Uri.ToString(), request.Content, request.Method, request.GetHeaders(), default, error);
                }
            }
            catch (HttpRequestException requestException)
            {
                // Request exception, can't reach server for instance
                var exceptionInfo = requestException.ToLogString();
                _log.Write(LogLevel.Warning, $"[{request.RequestId}] Request exception: " + exceptionInfo);
                return new WebCallResult<T>(null, null, null, null, request.Uri.ToString(), request.Content, request.Method, request.GetHeaders(), default, new WebError(exceptionInfo));
            }
            catch (OperationCanceledException canceledException)
            {
                if (cancellationToken != default && canceledException.CancellationToken == cancellationToken)
                {
                    // Cancellation token canceled by caller
                    _log.Write(LogLevel.Warning, $"[{request.RequestId}] Request canceled by cancellation token");
                    return new WebCallResult<T>(null, null, null, null, request.Uri.ToString(), request.Content, request.Method, request.GetHeaders(), default, new CancellationRequestedError());
                }
                else
                {
                    // Request timed out
                    _log.Write(LogLevel.Warning, $"[{request.RequestId}] Request timed out: " + canceledException.ToLogString());
                    return new WebCallResult<T>(null, null, null, null, request.Uri.ToString(), request.Content, request.Method, request.GetHeaders(), default, new WebError($"[{request.RequestId}] Request timed out"));
                }
            }
        }

        /// <summary>
        /// Can be used to parse an error even though response status indicates success. Some apis always return 200 OK, even though there is an error.
        /// When setting manualParseError to true this method will be called for each response to be able to check if the response is an error or not.
        /// If the response is an error this method should return the parsed error, else it should return null
        /// </summary>
        /// <param name="data">Received data</param>
        /// <returns>Null if not an error, Error otherwise</returns>
        protected virtual Task<ServerError?> TryParseErrorAsync(JToken data)
        {
            return Task.FromResult<ServerError?>(null);
        }

        /// <summary>
        /// Can be used to indicate that a request should be retried. Defaults to false. Make sure to retry a max number of times (based on the the tries parameter) or the request will retry forever.
        /// Note that this is always called; even when the request might be successful
        /// </summary>
        /// <typeparam name="T">WebCallResult type parameter</typeparam>
        /// <param name="callResult">The result of the call</param>
        /// <param name="tries">The current try number</param>
        /// <returns>True if call should retry, false if the call should return</returns>
        protected virtual Task<bool> ShouldRetryRequestAsync<T>(WebCallResult<T> callResult, int tries) => Task.FromResult(false);

        /// <summary>
        /// Creates a request object
        /// </summary>
        /// <param name="uri">The uri to send the request to</param>
        /// <param name="method">The method of the request</param>
        /// <param name="parameters">The parameters of the request</param>
        /// <param name="signed">Whether or not the request should be authenticated</param>
        /// <param name="parameterPosition">Where the parameters should be placed</param>
        /// <param name="arraySerialization">How array parameters should be serialized</param>
        /// <param name="requestId">Unique id of a request</param>
        /// <param name="additionalHeaders">Additional headers to send with the request</param>
        /// <returns></returns>
        protected virtual IRequest ConstructRequest(
            Uri uri,
            HttpMethod method,
            Dictionary<string, object>? parameters,
            bool signed,
            HttpMethodParameterPosition parameterPosition,
            ArrayParametersSerialization arraySerialization,
            int requestId,
            Dictionary<string, string>? additionalHeaders)
        {
            parameters ??= new Dictionary<string, object>();

            for (var i = 0; i < parameters.Count; i++)
            {
                var kvp = parameters.ElementAt(i);
                if (kvp.Value is Func<object> delegateValue)
                    parameters[kvp.Key] = delegateValue();
            }

            if (parameterPosition == HttpMethodParameterPosition.InUri)
            {
                foreach (var parameter in parameters)
                    uri = uri.AddQueryParmeter(parameter.Key, parameter.Value.ToString());
            }

            var headers = new Dictionary<string, string>();
            var uriParameters = parameterPosition == HttpMethodParameterPosition.InUri ? new SortedDictionary<string, object>(parameters) : new SortedDictionary<string, object>();
            var bodyParameters = parameterPosition == HttpMethodParameterPosition.InBody ? new SortedDictionary<string, object>(parameters) : new SortedDictionary<string, object>();
            if (AuthenticationProvider != null)
            {
                try
                {
                    AuthenticationProvider.AuthenticateRequest(
                        this,
                        uri,
                        method,
                        parameters,
                        signed,
                        arraySerialization,
                        parameterPosition,
                        out uriParameters,
                        out bodyParameters,
                        out headers);
                }
                catch (Exception ex)
                {
                    throw new Exception("Failed to authenticate request, make sure your API credentials are correct", ex);
                }
            }

            // Sanity check
            foreach (var param in parameters)
            {
                if (!uriParameters.ContainsKey(param.Key) && !bodyParameters.ContainsKey(param.Key))
                {
                    throw new Exception($"Missing parameter {param.Key} after authentication processing. AuthenticationProvider implementation " +
                        $"should return provided parameters in either the uri or body parameters output");
                }
            }

            // Add the auth parameters to the uri, start with a new URI to be able to sort the parameters including the auth parameters            
            uri = uri.SetParameters(uriParameters, arraySerialization);

            var request = RequestFactory.Create(method, uri, requestId);
            request.Accept = Constants.JsonContentHeader;

            foreach (var header in headers)
                request.AddHeader(header.Key, header.Value);

            if (additionalHeaders != null)
            {
                foreach (var header in additionalHeaders)
                    request.AddHeader(header.Key, header.Value);
            }

            if (StandardRequestHeaders != null)
            {
                foreach (var header in StandardRequestHeaders)
                {
                    // Only add it if it isn't overwritten
                    if (additionalHeaders?.ContainsKey(header.Key) != true)
                        request.AddHeader(header.Key, header.Value);
                }
            }

            if (parameterPosition == HttpMethodParameterPosition.InBody)
            {
                var contentType = requestBodyFormat == RequestBodyFormat.Json ? Constants.JsonContentHeader : Constants.FormContentHeader;
                if (bodyParameters.Any())
                    WriteParamBody(request, bodyParameters, contentType);
                else
                    request.SetContent(requestBodyEmptyContent, contentType);
            }

            return request;
        }

        /// <summary>
        /// Writes the parameters of the request to the request object body
        /// </summary>
        /// <param name="request">The request to set the parameters on</param>
        /// <param name="parameters">The parameters to set</param>
        /// <param name="contentType">The content type of the data</param>
        protected virtual void WriteParamBody(IRequest request, SortedDictionary<string, object> parameters, string contentType)
        {
            if (requestBodyFormat == RequestBodyFormat.Json)
            {
                // Write the parameters as json in the body
                var stringData = JsonConvert.SerializeObject(parameters);
                request.SetContent(stringData, contentType);
            }
            else if (requestBodyFormat == RequestBodyFormat.FormData)
            {
                // Write the parameters as form data in the body
                var stringData = parameters.ToFormData();
                request.SetContent(stringData, contentType);
            }
        }

        /// <summary>
        /// Parse an error response from the server. Only used when server returns a status other than Success(200)
        /// </summary>
        /// <param name="error">The string the request returned</param>
        /// <returns></returns>
        protected virtual Error ParseErrorResponse(JToken error)
        {
            return new ServerError(error.ToString());
        }

        /// <summary>
        /// Retrieve the server time for the purpose of syncing time between client and server to prevent authentication issues
        /// </summary>
        /// <returns>Server time</returns>
        protected virtual Task<WebCallResult<DateTime>> GetServerTimestampAsync() => throw new NotImplementedException();

        internal async Task<WebCallResult<bool>> SyncTimeAsync()
        {
            var timeSyncParams = GetTimeSyncInfo();
            if (timeSyncParams == null)
                return new WebCallResult<bool>(null, null, null, null, null, null, null, null, true, null);

            if (await timeSyncParams.TimeSyncState.Semaphore.WaitAsync(0).ConfigureAwait(false))
            {
                if (!timeSyncParams.SyncTime || (DateTime.UtcNow - timeSyncParams.TimeSyncState.LastSyncTime < timeSyncParams.RecalculationInterval))
                {
                    timeSyncParams.TimeSyncState.Semaphore.Release();
                    return new WebCallResult<bool>(null, null, null, null, null, null, null, null, true, null);
                }

                var localTime = DateTime.UtcNow;
                var result = await GetServerTimestampAsync().ConfigureAwait(false);
                if (!result)
                {
                    timeSyncParams.TimeSyncState.Semaphore.Release();
                    return result.As(false);
                }

                if (TotalRequestsMade == 1)
                {
                    // If this was the first request make another one to calculate the offset since the first one can be slower
                    localTime = DateTime.UtcNow;
                    result = await GetServerTimestampAsync().ConfigureAwait(false);
                    if (!result)
                    {
                        timeSyncParams.TimeSyncState.Semaphore.Release();
                        return result.As(false);
                    }
                }

                // Calculate time offset between local and server
                var offset = result.Data - (localTime.AddMilliseconds(result.ResponseTime!.Value.TotalMilliseconds / 2));
                timeSyncParams.UpdateTimeOffset(offset);
                timeSyncParams.TimeSyncState.Semaphore.Release();
            }

            return new WebCallResult<bool>(null, null, null, null, null, null, null, null, true, null);
        }
    }
}
