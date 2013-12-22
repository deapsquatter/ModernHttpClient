using System;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Net;
using System.Net.Http;
using OkHttp;

namespace ModernHttpClient
{
    public class OkHttpNetworkHandler : HttpMessageHandler
    {
        static readonly object xamarinLock = new object();
        readonly OkHttpClient client = new OkHttpClient();
        readonly bool throwOnCaptiveNetwork;

        public OkHttpNetworkHandler() : this(false) {}

        public OkHttpNetworkHandler(bool throwOnCaptiveNetwork)
        {
            this.throwOnCaptiveNetwork = throwOnCaptiveNetwork;
        }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            try {
                return await InternalSendAsync(request, cancellationToken);
            } catch(Exception e) {
                JavaExceptionMapper(e);
                throw e;
            }
        }
        private static void JavaExceptionMapper(Exception e)
        {
            if (e is Java.Net.UnknownHostException)
                throw new WebException("Name resolution failure", e, WebExceptionStatus.NameResolutionFailure, null);
            if (e is Java.IO.IOException)
                throw new WebException("IO Exception", e, WebExceptionStatus.ConnectFailure, null);
        }
        protected async Task<HttpResponseMessage> InternalSendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var java_uri = request.RequestUri.GetComponents(UriComponents.AbsoluteUri, UriFormat.UriEscaped);
            var url = new Java.Net.URL(java_uri);
            var rq = client.Open(url);
            rq.RequestMethod = request.Method.Method.ToUpperInvariant();

            foreach (var kvp in request.Headers) { rq.SetRequestProperty(kvp.Key, kvp.Value.FirstOrDefault()); }

            if (request.Content != null) {
                foreach (var kvp in request.Content.Headers) { rq.SetRequestProperty (kvp.Key, kvp.Value.FirstOrDefault ()); }

                await Task.Run(async () => {
                    var contentStream = await request.Content.ReadAsStreamAsync().ConfigureAwait(false);
                    await copyToAsync(contentStream, rq.OutputStream, cancellationToken).ConfigureAwait(false);
                }, cancellationToken).ConfigureAwait(false);

                rq.OutputStream.Close();
            }

            return await Task.Run (() => {
                // NB: This is the line that blocks until we have headers
                var ret = new HttpResponseMessage((HttpStatusCode)rq.ResponseCode);
                // Test to see if we're being redirected (i.e. in a captive network)
                if (throwOnCaptiveNetwork && (url.Host != rq.URL.Host)) {
                    throw new WebException("Hostnames don't match, you are probably on a captive network");
                }

                cancellationToken.ThrowIfCancellationRequested();

                ret.Content = new StreamContent(new ConcatenatingStream(new Func<Stream>[] {
                    () => rq.InputStream,
                    () => rq.ErrorStream ?? new MemoryStream (),
                }, true, JavaExceptionMapper));

                //the implicit handling of Java.Lang.String => string conversion
                //is broken badly.  effectively it is a race condition to be doing
                //operations with identical string instances (string interning will cause this)
                //on different threads
                lock(xamarinLock) {
                    var headers = rq.HeaderFields;
                    foreach (var k in headers.Keys) {
                        if(k == null)
                            continue;
                        foreach (var v in headers[k]) {
                            ret.Headers.TryAddWithoutValidation(k, v);
                            ret.Content.Headers.TryAddWithoutValidation(k, v);
                        }
                    }
                }
                cancellationToken.Register (ret.Content.Dispose);

                ret.RequestMessage = request;
                return ret;
            }, cancellationToken).ConfigureAwait(false);
        }

        async Task copyToAsync(Stream source, Stream target, CancellationToken ct)
        {
            await Task.Run(async () => {
                var buf = new byte[4096];
                var read = 0;

                do {
                    read = await source.ReadAsync(buf, 0, 4096).ConfigureAwait(false);

                    if (read > 0) {
                        target.Write(buf, 0, read);
                    }
                } while (!ct.IsCancellationRequested && read > 0);

                ct.ThrowIfCancellationRequested();
            }, ct).ConfigureAwait(false);
        }
    }
}
