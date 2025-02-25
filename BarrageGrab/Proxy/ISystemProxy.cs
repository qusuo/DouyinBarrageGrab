﻿using System;
using BarrageGrab.Proxy.ProxyEventArgs;

namespace BarrageGrab.Proxy
{
    internal interface ISystemProxy : IDisposable
    {
        event EventHandler<WsMessageEventArgs> OnWebSocketData;

        event EventHandler<HttpResponseEventArgs> OnFetchResponse;

        /// <summary>
        /// 开始监听
        /// </summary>
        void Start();
    }    
}