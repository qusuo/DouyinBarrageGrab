using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using BarrageGrab.Modles;
using BarrageGrab.Proxy.ProxyEventArgs;
using ColorConsole;
using Titanium.Web.Proxy;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.StreamExtended.Network;

namespace BarrageGrab.Proxy
{
    internal class TitaniumProxy : SystemProxy
    {
        ProxyServer proxyServer = null;
        ExplicitProxyEndPoint explicitEndPoint = null;
        ExternalProxy upStreamProxy = null;

        const string SCRIPT_HOST = "lf-cdn-tos.bytescm.com";
        const string LIVE_HOST = "live.douyin.com";
        const string DOUYIN_HOST = "www.douyin.com";
        const string USER_INFO_PATH = "/webcast/user/me/";
        const string BARRAGE_POOL_PATH = "/webcast/im/fetch";
        static ConsoleWriter console = new ConsoleWriter();

        public TitaniumProxy()
        {
            console.WriteLine("正在初始化代理...", ConsoleColor.Green);
            //注册系统代理
            //RegisterSystemProxy();
            proxyServer = new ProxyServer();

            proxyServer.ReuseSocket = false;
            proxyServer.EnableConnectionPool = true;
            proxyServer.ForwardToUpstreamGateway = true;
            proxyServer.CertificateManager.SaveFakeCertificates = true;

            proxyServer.CertificateManager.RootCertificate = proxyServer.CertificateManager.LoadRootCertificate();
            if (proxyServer.CertificateManager.RootCertificate == null)
            {
                Console.WriteLine("正在进行证书安装，需要安装证书才可进行https解密，若有提示请确定");
                proxyServer.CertificateManager.CreateRootCertificate();
            }

            //设置上游代理地址
            var upstreamProxyAddr = Appsetting.Current.UpstreamProxy;
            if (!upstreamProxyAddr.IsNullOrWhiteSpace())
            {
                upStreamProxy = new ExternalProxy()
                {
                    HostName = upstreamProxyAddr.Split(':')[0],
                    Port = int.Parse(upstreamProxyAddr.Split(':')[1]),
                    ProxyType = ExternalProxyType.Http
                };
                proxyServer.UpStreamHttpProxy = upStreamProxy;
                proxyServer.UpStreamHttpsProxy = upStreamProxy;
            }

            proxyServer.ServerCertificateValidationCallback += ProxyServer_ServerCertificateValidationCallback;
            proxyServer.BeforeResponse += ProxyServer_BeforeResponse;
            //proxyServer.AfterResponse += ProxyServer_AfterResponse;


            explicitEndPoint = new ExplicitProxyEndPoint(IPAddress.Any, ProxyPort, true);
            explicitEndPoint.BeforeTunnelConnectRequest += ExplicitEndPoint_BeforeTunnelConnectRequest;
            proxyServer.AddEndPoint(explicitEndPoint);
        }

        private async Task ProxyServer_BeforeResponse(object sender, SessionEventArgs e)
        {
            console.WriteLine($"正在处理响应 {e.HttpClient.Request.RequestUri}...", ConsoleColor.Green);
            string uri = e.HttpClient.Request.RequestUri.ToString();
            string hostname = e.HttpClient.Request.RequestUri.Host;
            var processid = e.HttpClient.ProcessId.Value;
            var processName = base.GetProcessName(processid);
            var contentType = e.HttpClient.Response.ContentType ?? "";

            //处理弹幕
            await HookBarrage(e);

            //处理JS注入
            await HookPageAsync(e);

            //处理脚本拦截修改
            await HookScriptAsync(e);
        }

        private async Task HookBarrage(SessionEventArgs e)
        {
            console.WriteLine($"正在处理弹幕 {e.HttpClient.Request.RequestUri}...", ConsoleColor.Green);
            string uri = e.HttpClient.Request.RequestUri.ToString();
            string hostname = e.HttpClient.Request.RequestUri.Host;
            var processid = e.HttpClient.ProcessId.Value;
            var processName = base.GetProcessName(processid);
            var contentType = e.HttpClient.Response.ContentType ?? "";

            //ws 方式
            if (
                e.HttpClient.ConnectRequest?.TunnelType == TunnelType.Websocket &&
                hostname.StartsWith("webcast")
               )
            {
                e.DataReceived += WebSocket_DataReceived;
            }
            //轮询方式(当抖音ws连接断开后，客户端也会降级使用轮询模式获取弹幕)
            if (uri.Contains(BARRAGE_POOL_PATH) && contentType.Contains("application/protobuffer"))
            {
                var payload = await e.GetResponseBody();
                base.FireOnFetchResponse(new HttpResponseEventArgs()
                {
                    HttpClient = e.HttpClient,
                    ProcessID = processid,
                    HostName = hostname,
                    ProcessName = base.GetProcessName(processid),
                });
            }
        }

        /// <summary>
        /// 获取配置的注入的js
        /// </summary>
        /// <param name="name"></param>
        /// <returns></returns>
        private string GetInjectScript(string name)
        {
            console.WriteLine($"正在获取注入脚本 {name}...", ConsoleColor.Green);
            //获取exe所在目录路径            
            var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Scripts", "inject", name + ".js");
            if (File.Exists(path))
            {
                return File.ReadAllText(path);
            }
            return null;
        }

        private async Task HookPageAsync(SessionEventArgs e)
        {
            console.WriteLine($"正在处理页面 {e.HttpClient.Request.RequestUri}...", ConsoleColor.Green);
            string uri = e.HttpClient.Request.RequestUri.ToString();
            string hostname = e.HttpClient.Request.RequestUri.Host;
            string url = e.HttpClient.Request.Url;
            var urlNoQuery = url.Split('?')[0];
            var processid = e.HttpClient.ProcessId.Value;
            var processName = base.GetProcessName(processid);
            var liveRoomMactch = Regex.Match(uri.Trim(), @".*:\/\/live.douyin\.com\/(\d+)");
            var contentType = e.HttpClient.Response.ContentType ?? "";

            //检测是否为dom页，用于脚本注入
            if (contentType.Contains("text/html"))
            {
                //如果响应头含有 CSP(https://blog.csdn.net/qq_30436011/article/details/127485927 会阻止内嵌脚本执行) 则删除
                var csp = e.HttpClient.Response.Headers.GetFirstHeader("Content-Security-Policy");
                if (csp != null)
                {
                    e.HttpClient.Response.Headers.RemoveHeader("Content-Security-Policy");
                }

                //获取 content-type                
                if (liveRoomMactch.Success)
                {
                    string webrid = liveRoomMactch.Groups[1].Value;
                    //获取直播页注入js
                    string liveRoomInjectScript = GetInjectScript("livePage");

                    //注入上下文变量;
                    var scriptContext = $"const PROCESS_NAME = '{processName}';\n";
                    liveRoomInjectScript = scriptContext + liveRoomInjectScript;

                    if (!liveRoomInjectScript.IsNullOrWhiteSpace())
                    {
                        //利用 HtmlAgilityPack 在尾部注入script 标签
                        var html = await e.GetResponseBodyAsString();

                        (int code, string msg) = RoomInfo.TryParseRoomPageHtml(html, out var roominfo);

                        if (code == 0)
                        {
                            Logger.LogInfo($"直播页{webrid}房间信息已采集到缓存");
                            roominfo.WebRoomId = webrid;
                            roominfo.LiveUrl = url;
                            AppRuntime.RoomCaches.AddRoomInfoCache(roominfo);
                        }
                        else
                        {
                            roominfo = new RoomInfo();
                            roominfo.WebRoomId = webrid;
                            roominfo.LiveUrl = url;
                            //正则匹配主播标题
                            //<div class="st8eGKi4" data-e2e="live-room-nickname">和平精英小夜y</div>
                            var match = Regex.Match(html, @"(?<=live-room-nickname""\>).+(?=<\/div>)");
                            if (match.Success)
                            {
                                roominfo.Owner = new RoomInfo.RoomAnchor()
                                {
                                    Nickname = match.Value,
                                    UserId = "-1"
                                };
                            }
                            AppRuntime.RoomCaches.AddRoomInfoCache(roominfo);
                        }
                        try
                        {
                            var doc = new HtmlAgilityPack.HtmlDocument();
                            doc.LoadHtml(html);
                            //找到body标签,在尾部注入script标签
                            var body = doc.DocumentNode.SelectSingleNode("//body");
                            if (body != null)
                            {
                                var script = doc.CreateElement("script");
                                script.InnerHtml = liveRoomInjectScript;
                                body.AppendChild(script);
                                var newHtml = doc.DocumentNode.OuterHtml;
                                e.SetResponseBodyString(newHtml);
                                console.WriteLine($"直播页{urlNoQuery},用户脚本已成功注入!\n", ConsoleColor.Green);
                            }
                        }
                        catch (Exception ex)
                        {
                            Logger.LogError(ex, $"直播页{url},用户脚本注入异常");
                        }
                    }
                }
            }
        }

        private async Task HookScriptAsync(SessionEventArgs e)
        {
            console.WriteLine($"正在检测是否为JS页面 {e.HttpClient.Request.RequestUri.Host}...", ConsoleColor.Green);
            string uri = e.HttpClient.Request.RequestUri.ToString();
            string hostname = e.HttpClient.Request.RequestUri.Host;
            string url = e.HttpClient.Request.Url;
            var urlNoQuery = url.Split('?')[0];
            var processid = e.HttpClient.ProcessId.Value;
            var processName = base.GetProcessName(processid);
            var contentType = e.HttpClient.Response.ContentType ?? "";

            //判断响应内容是否为js application/javascript
            if (contentType != null &&
                contentType.Trim().ToLower().Contains("application/javascript") &&
                hostname == SCRIPT_HOST
                && e.HttpClient.Response.StatusCode == 200
                //这俩个进程不需要注入
                && processName != "直播伴侣"
                && processName != "douyin"
                )
            {
                var js = await e.GetResponseBodyAsString();
                //var reg2 = new Regex(@"if\(!N.DJ\(\)&&(?<variable>\S).current\)\{"); //版本1,已过时
                //if(!(0,k.DJ)()&amp;&amp;_.current){

                var reg2 = new Regex(@"if\s*\(\s*\!\s*(?<v1>[\s\S]+\s*\.\s*DJ\s*\))\s*\(\s*\)\s*&\s*&\s*(?<v2>\S)\s*.\s*current\s*\)\s*\{");
                var match = reg2.Match(js);
                if (match.Success)
                {
                    js = reg2.Replace(js, "if(!${v1}()&&${v2}.current){return;");
                    e.SetResponseBodyString(js);
                    console.WriteLine($"已成功绕过JS页面无操作检测 {urlNoQuery}\n", ConsoleColor.Green);
                    return;
                }
            }
        }

        private Task ProxyServer_ServerCertificateValidationCallback(object sender, CertificateValidationEventArgs e)
        {
            Console.WriteLine($"正在验证证书 {e.SslPolicyErrors}...", ConsoleColor.Green);
            // set IsValid to true/false based on Certificate Errors
            if (e.SslPolicyErrors == SslPolicyErrors.None)
            {
                e.IsValid = true;
            }
            return Task.CompletedTask;
        }

#pragma warning disable CS1998 // Async method lacks 'await' operators and will run synchronously
        private async Task ExplicitEndPoint_BeforeTunnelConnectRequest(object sender, TunnelConnectSessionEventArgs e)
#pragma warning restore CS1998 // Async method lacks 'await' operators and will run synchronously
        {
            console.WriteLine($"正在连接 {e.HttpClient.Request.RequestUri.Host}...", ConsoleColor.Green);

            string url = e.HttpClient.Request.RequestUri.ToString();
            string hostname = e.HttpClient.Request.RequestUri.Host;

            console.WriteLine($"正在连接 {e.HttpClient.Request.RequestUri.Host}... start", ConsoleColor.Green);
            //用于检测修改js脚本
            if (hostname == SCRIPT_HOST)
            {
                console.WriteLine($"正在连接 {e.HttpClient.Request.RequestUri.Host}... 0", ConsoleColor.Green);
                e.DecryptSsl = true;
                return;
            }

            console.WriteLine($"正在连接 {e.HttpClient.Request.RequestUri.Host}... 1", ConsoleColor.Green);
            //用于采集当前用户信息数据，供本地调用
            if (hostname == LIVE_HOST)
            {
                console.WriteLine($"正在连接 {e.HttpClient.Request.RequestUri.Host}... 1.1", ConsoleColor.Green);
                e.DecryptSsl = true;
                return;
            }

            console.WriteLine($"正在连接 {e.HttpClient.Request.RequestUri.Host}... 2", ConsoleColor.Green);
            if (!CheckHost(hostname))
            {
                console.WriteLine($"正在连接 {e.HttpClient.Request.RequestUri.Host}... 3", ConsoleColor.Green);
                e.DecryptSsl = false;
            }
        }

        protected override bool CheckHost(string host)
        {
            console.WriteLine($"正在检测域名 {host}...", ConsoleColor.Green);
            host = host.Trim().ToLower();
            var succ = base.CheckHost(host);

            console.WriteLine($"正在检测域名 {host}... {succ}...{succ || host == SCRIPT_HOST || host == LIVE_HOST}", ConsoleColor.Green);
            return succ || host == SCRIPT_HOST || host == LIVE_HOST;
        }

#pragma warning disable CS1998 // Async method lacks 'await' operators and will run synchronously
        private async void WebSocket_DataReceived(object sender, DataEventArgs e)
#pragma warning restore CS1998 // Async method lacks 'await' operators and will run synchronously
        {
            console.WriteLine("收到WebSocket数据包", ConsoleColor.Green);

            var args = (SessionEventArgs)sender;

            string hostname = args.HttpClient.Request.RequestUri.Host;

            var processid = args.HttpClient.ProcessId.Value;

            List<byte> messageData = new List<byte>();

            try
            {
                foreach (var frame in args.WebSocketDecoderReceive.Decode(e.Buffer, e.Offset, e.Count))
                {
                    if (frame.OpCode == WebsocketOpCode.Continuation)
                    {
                        messageData.AddRange(frame.Data.ToArray());
                        continue;
                    }
                    else
                    {
                        //读取完毕
                        byte[] payload;
                        if (messageData.Count > 0)
                        {
                            messageData.AddRange(frame.Data.ToArray());
                            payload = messageData.ToArray();
                            messageData.Clear();
                        }
                        else
                        {
                            payload = frame.Data.ToArray();
                        }

                        base.FireWsEvent(new WsMessageEventArgs()
                        {
                            ProcessID = processid,
                            HostName = hostname,
                            Payload = payload,
                            ProcessName = base.GetProcessName(processid)
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("解析某个WebSocket包出错：" + ex.Message);
            }

            if (messageData.Count > 0)
            {
                // 没有收到 WebSocket 帧的结束帧，抛出异常或者进行处理
            }

            console.WriteLine("收到WebSocket数据包 end", ConsoleColor.Green);
        }

        /// <summary>
        /// 释放资源，关闭系统代理
        /// </summary>
        override public void Dispose()
        {
            console.WriteLine("正在释放资源...", ConsoleColor.Green);

            proxyServer.Stop();
            proxyServer.Dispose();
            if (Appsetting.Current.UsedProxy)
            {
                CloseSystemProxy();
            }
        }

        /// <summary>
        /// 启动监听
        /// </summary>
        override public void Start()
        {
            console.WriteLine("正在启动代理...", ConsoleColor.Green);
            proxyServer.Start(Appsetting.Current.UsedProxy);
            if (Appsetting.Current.UsedProxy)
            {
                proxyServer.SetAsSystemHttpProxy(explicitEndPoint);
                proxyServer.SetAsSystemHttpsProxy(explicitEndPoint);
            }
        }
    }
}
