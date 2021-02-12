using Azure.Core.Pipeline;
using NetworkToolkit.Connections;
using NetworkToolkit.Http.Primitives;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace Azure.Core.Http.NoAlloc
{
    public class NoAllocTransport : HttpPipelineTransport, IAsyncDisposable
    {
        ConnectionFactory connectionFactory = new SocketConnectionFactory();

        public override Request CreateRequest() => new NoAllocRequest();

        public async ValueTask DisposeAsync() {
            await connectionFactory.DisposeAsync();
            connectionFactory = null;
        }

        public override void Process(HttpMessage message) {
            // ISSUE: if we don't have sync APIs, we would 
            ProcessAsync(message).GetAwaiter().GetResult();
        }

        public override async ValueTask ProcessAsync(HttpMessage message) {

            var pipelineRequest = message.Request;
            var host = pipelineRequest.Uri.Host;

            HttpMethod method = MapMethod(pipelineRequest.Method);

            Connection connection = await connectionFactory.ConnectAsync(new DnsEndPoint(host, 443));
            HttpConnection httpConnection = new Http1Connection(connection, HttpPrimitiveVersion.Version11);
            await using (ValueHttpRequest request = (await httpConnection.CreateNewRequestAsync(HttpPrimitiveVersion.Version11, HttpVersionPolicy.RequestVersionExact)).Value) {

                RequestContent pipelineContent = pipelineRequest.Content;
                long contentLength = 0;
                if (pipelineContent != null) {
                    if (!pipelineContent.TryComputeLength(out contentLength)) {
                        throw new NotImplementedException();
                    }
                }
                request.ConfigureRequest(contentLength: contentLength, hasTrailingHeaders: false);

                request.WriteRequest(method, pipelineRequest.Uri.ToUri());

                var pipelineHeaders = pipelineRequest.Headers;
                foreach (var header in pipelineHeaders) {
                    if (header.Value == null) { // TODO: the request ID might be null (if SDK user does not set it). I don't know what to do with this.
                        throw new NotImplementedException("HttpPipeline.CreateClientRequestIdScope has to be called in app code");
                    }
                }

                checked {
                    if (contentLength != 0) {
                        using var ms = new MemoryStream((int)contentLength); // TODO: can the buffer be disposed here?
                        await pipelineContent.WriteToAsync(ms, message.CancellationToken);
                        await request.WriteContentAsync(ms.GetBuffer().AsMemory(0, (int)ms.Length));
                    }
                }

                await request.CompleteRequestAsync();

                var response = new NoAllocResponse();
                message.Response = response;

                await request.ReadToFinalResponseAsync();

                response.SetStatus((int)request.StatusCode);

                if (await request.ReadToHeadersAsync()) {
                    await request.ReadHeadersAsync(response, state: null);
                }

                if (await request.ReadToContentAsync()) {
                    var buffer = new byte[4096];
                    int readLen;
                    do {
                        while ((readLen = await request.ReadContentAsync(buffer)) != 0) {
                            if(readLen < 4096) {
                                response.ContentStream = new MemoryStream(buffer, 0, readLen);
                            }
                            else {
                                throw new NotImplementedException();
                            }
                        }
                    }
                    while (await request.ReadToNextContentAsync());
                }

                if (await request.ReadToTrailingHeadersAsync()) {
                    await request.ReadHeadersAsync(response, state: null);
                }
            }
        }

        private static HttpMethod MapMethod(RequestMethod method) {
            if (method == RequestMethod.Get) { return HttpMethod.Get; }
            if (method == RequestMethod.Put) { return HttpMethod.Put; }
            if (method == RequestMethod.Post) { return HttpMethod.Post; }
            throw new NotImplementedException();
        }
    }

    class NoAllocResponse : Response, IHttpHeadersSink
    {
        Dictionary<string, string> _headers = new Dictionary<string, string>();
        int _status;
        string _reason;
        string _requestId;

        public override int Status => _status;
        internal void SetStatus(int status, string reason = null) {
            _status = status;
            _reason = reason;
        }

        public override string ReasonPhrase => _reason;

        public override Stream ContentStream { get; set; }

        public override string ClientRequestId { 
            get => _requestId ??= Guid.NewGuid().ToString();
            set => _requestId = value;
        }

        public override void Dispose() {
            _headers.Clear();
            _headers = null;
        }

        public void OnHeader(object state, ReadOnlySpan<byte> headerName, ReadOnlySpan<byte> headerValue) {
            var name = Encoding.UTF8.GetString(headerName);
            var value = Encoding.UTF8.GetString(headerValue);
            _headers.Add(name, value);
        }

        protected override bool ContainsHeader(string name) => _headers.ContainsKey(name);

        protected override IEnumerable<HttpHeader> EnumerateHeaders() {
            foreach(var h in _headers) {
                yield return new HttpHeader(h.Key, h.Value);
            }
        }

        protected override bool TryGetHeader(string name, [NotNullWhen(true)] out string value) {
            return _headers.TryGetValue(name, out value);
        }

        protected override bool TryGetHeaderValues(string name, [NotNullWhen(true)] out IEnumerable<string> values) {
            if(_headers.TryGetValue(name, out string value)) {
                values = new string[] { value };
                return true;
            }
            values = null;
            return false;
        }
    }
    class NoAllocRequest : Request
    {
        Dictionary<string, HttpHeader> _headers = new Dictionary<string, HttpHeader>();
        public override string ClientRequestId { get; set; }

        public override void Dispose() {
        }

        protected override void AddHeader(string name, string value) {
            _headers.Add(name, new HttpHeader(name, value));
        }

        protected override bool ContainsHeader(string name) {
            throw new NotImplementedException();
        }

        protected override IEnumerable<HttpHeader> EnumerateHeaders() => _headers.Values;

        protected override bool RemoveHeader(string name) {
            return _headers.Remove(name);
        }

        protected override bool TryGetHeader(string name, [NotNullWhen(true)] out string value) {
            if(!_headers.TryGetValue(name, out var header)) {
                value = null;
                return false;
            }
            value = header.Value;
            return true;
        }

        protected override bool TryGetHeaderValues(string name, [NotNullWhen(true)] out IEnumerable<string> values) {
            if (!_headers.TryGetValue(name, out var header)) {
                values = null;
                return false;
            }
            values = new string[] { header.Value };
            return true;
        }
    }
}
