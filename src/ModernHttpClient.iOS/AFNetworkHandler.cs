using System;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using System.Threading;
using System.Collections.Generic;
using AFNetworking;
using MonoTouch.Foundation;
using System.IO;
using System.Net;

namespace ModernHttpClient
{
    public class AFNetworkHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            try {
                return await InternalSendAsync(request, cancellationToken);
            } catch(Exception e) {
                IosExceptionMapper(e);
                throw e;
            }
        }
        private void IosExceptionMapper(Exception e)
        {
            //just map everything to a temporary exception
            throw new WebException("IO Exception", e, WebExceptionStatus.ConnectFailure, null);
        }
        protected async Task<HttpResponseMessage> InternalSendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var headers = request.Headers as IEnumerable<KeyValuePair<string, IEnumerable<string>>>;
            var ms = new MemoryStream();

            if (request.Content != null) {
                await request.Content.CopyToAsync(ms).ConfigureAwait(false);
                headers = headers.Union(request.Content.Headers);
            }

            var rq = new NSMutableUrlRequest() {
                AllowsCellularAccess = true,
                Body = NSData.FromArray(ms.ToArray()),
                CachePolicy = NSUrlRequestCachePolicy.UseProtocolCachePolicy,
                Headers = headers.Aggregate(new NSMutableDictionary(), (acc, x) => {
                    acc.Add(new NSString(x.Key), new NSString(x.Value.LastOrDefault()));
                    return acc;
                }),
                HttpMethod = request.Method.ToString().ToUpperInvariant(),
                Url = NSUrl.FromString(request.RequestUri.AbsoluteUri),
            };

            var host = request.RequestUri.GetLeftPart(UriPartial.Authority);
            var op = default(AFHTTPRequestOperation);
            var err = default(NSError);
            var handler = new AFHTTPClient(new NSUrl(host));

            var blockingTcs = new TaskCompletionSource<Stream>();
            var ret= default(HttpResponseMessage);

            //responseData field is only valid during the callback.  after that the connection and buffer
            //are reused.  we either need to map through an nsoutputstream or copy the data.
            //i will do an nsoutputstream style thing later, but its somewhat tricky as .net lacks
            //anything like a PipeStream that doesn't consume a file descriptor and fully supports Async.
            try {
                op = await enqueueOperation(handler, new AFHTTPRequestOperation(rq), cancellationToken, (s) => blockingTcs.SetResult(s), ex => {
                    if (ex is ApplicationException) {
                        err = (NSError)ex.Data["err"];
                    }

                    if (ret == null) {
                        return;
                    }

                    ret.ReasonPhrase = (err != null ? err.LocalizedDescription : null);
                });
            } catch (ApplicationException ex) {
                op = (AFHTTPRequestOperation)ex.Data["op"];
                err = (NSError)ex.Data["err"];
            }

            var resp = (NSHttpUrlResponse)op.Response;

            if (err != null && resp == null && err.Domain == NSError.NSUrlErrorDomain) {
                throw new WebException (err.LocalizedDescription, WebExceptionStatus.NameResolutionFailure);
            }

            if (op.IsCancelled) {
                throw new TaskCanceledException();
            }

            var httpContent = new StreamContent (await blockingTcs.Task);

            ret = new HttpResponseMessage((HttpStatusCode)resp.StatusCode) {
                Content = httpContent,
                RequestMessage = request,
                ReasonPhrase = (err != null ? err.LocalizedDescription : null),
            };

            foreach(var v in resp.AllHeaderFields) {
                ret.Headers.TryAddWithoutValidation(v.Key.ToString(), v.Value.ToString());
                ret.Content.Headers.TryAddWithoutValidation(v.Key.ToString(), v.Value.ToString());
            }

            return ret;
        }

        static MemoryStream ToMemoryStream (NSData data)
        {
            if(data == null || data.Length == 0 || data.Bytes == IntPtr.Zero)
                return new MemoryStream();
            byte[] bytes = new byte[data.Length];

            System.Runtime.InteropServices.Marshal.Copy(data.Bytes, bytes, 0, Convert.ToInt32(data.Length));

            return new MemoryStream(bytes);
        }

        Task<AFHTTPRequestOperation> enqueueOperation(AFHTTPClient handler, AFHTTPRequestOperation operation, CancellationToken cancelToken, Action<Stream> onCompleted, Action<Exception> onError)
        {
            var tcs = new TaskCompletionSource<AFHTTPRequestOperation>();
            if (cancelToken.IsCancellationRequested) {
                tcs.SetCanceled();
                return tcs.Task;
            }

            bool completed = false;

            operation.SetDownloadProgressBlock((a, b, c) => {
                // NB: We're totally cheating here, we just happen to know
                // that we're guaranteed to have response headers after the
                // first time we get progress.
                if (completed) return;

                completed = true;
                tcs.SetResult(operation);
            });

            operation.SetCompletionBlockWithSuccess(
                (op, _) => {
                    if (!completed) {
                        completed = true;
                        tcs.SetResult(operation);
                    }

                    onCompleted(ToMemoryStream(op.ResponseData));
                },
                (op, err) => {
                    var ex = new ApplicationException();
                    ex.Data.Add("op", op);
                    ex.Data.Add("err", err);

                    onCompleted(ToMemoryStream(op.ResponseData));
                    if (completed) {
                        onError(ex);
                        return;
                    }

                    // NB: Secret Handshake is Secret
                    completed = true;
                    tcs.SetException(ex);
                });

            handler.EnqueueHTTPRequestOperation(operation);
            cancelToken.Register(() => {
                if (completed) return;

                completed = true;
                operation.Cancel();
                tcs.SetCanceled();
            });

            return tcs.Task;
        }
    }
}
