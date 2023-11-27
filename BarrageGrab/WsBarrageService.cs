﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Timers;
using ColorConsole;
using Fleck;
using Newtonsoft.Json;
using BarrageGrab.Modles.JsonEntity;
using BarrageGrab.Modles.ProtoEntity;
using System.Net.Sockets;

namespace BarrageGrab
{

    /// <summary>
    /// 弹幕服务
    /// </summary>
    public class WsBarrageService
    {
        WebSocketServer socketServer;
        Dictionary<string, UserState> socketList = new Dictionary<string, UserState>();
        //礼物计数缓存
        Dictionary<string, Tuple<int, DateTime>> giftCountCache = new Dictionary<string, Tuple<int, DateTime>>();
        Timer dieout = new Timer(10000);
        Timer giftCountTimer = new Timer(10000);
        ConsoleWriter console = new ConsoleWriter();
        WssBarrageGrab grab = new WssBarrageGrab();
        Appsetting Appsetting = Appsetting.Current;

        /// <summary>
        /// 服务关闭后触发
        /// </summary>
        public event EventHandler OnClose;

        public WsBarrageService()
        {
            console.WriteLine($"当前版本：WsBarrageService", ConsoleColor.Green);

            var socket = new WebSocketServer($"ws://0.0.0.0:{Appsetting.WsProt}");
            socket.RestartAfterListenError = true;//异常重启

            dieout.Elapsed += Dieout_Elapsed;
            giftCountTimer.Elapsed += GiftCountTimer_Elapsed;

            this.grab.OnChatMessage += Grab_OnChatMessage;
            this.grab.OnLikeMessage += Grab_OnLikeMessage;
            this.grab.OnMemberMessage += Grab_OnMemberMessage;
            this.grab.OnSocialMessage += Grab_OnSocialMessage;
            this.grab.OnSocialMessage += Grab_OnShardMessage;
            this.grab.OnGiftMessage += Grab_OnGiftMessage;
            this.grab.OnRoomUserSeqMessage += Grab_OnRoomUserSeqMessage;
            this.grab.OnFansclubMessage += Grab_OnFansclubMessage; ;
            this.grab.OnControlMessage += Grab_OnControlMessage;

            this.socketServer = socket;
            dieout.Start();
            giftCountTimer.Start();
        }

        private void GiftCountTimer_Elapsed(object sender, ElapsedEventArgs e)
        {
            console.WriteLine($"礼物计数缓存清理", ConsoleColor.Green);

            var now = DateTime.Now;
            var timeOutKeys = giftCountCache.Where(w => w.Value.Item2 < now.AddSeconds(-10) || w.Value == null).Select(s => s.Key).ToList();

            //淘汰过期的礼物计数缓存
            lock (giftCountCache)
            {
                timeOutKeys.ForEach(key =>
                {
                    giftCountCache.Remove(key);

                });
            }

            console.WriteLine($"礼物计数缓存清理 end", ConsoleColor.Green);
        }

        private bool CheckRoomId(long roomid)
        {
            console.WriteLine($"检查房间号：{roomid}", ConsoleColor.Green);

            if (Appsetting.RoomIds.Length == 0) return true;
            return Appsetting.RoomIds.Any(a => a == roomid);
        }
        
        //解析用户
        private MsgUser GetUser(User data)
        {
            console.WriteLine($"解析用户：{data.Id} {data.Nickname}", ConsoleColor.Green);

            MsgUser user = new MsgUser()
            {
                DisplayId = data.displayId,
                ShortId = data.shortId,
                Gender = data.Gender,
                Id = data.Id,
                Level = data.Level,
                PayLevel = (int)(data.payGrade?.Level ?? 0),
                Nickname = data.Nickname,
                HeadImgUrl = data.avatarThumb.urlLists.FirstOrDefault() ?? "",
                SecUid = data.sec_uid,
                FollowerCount = data.followInfo.followerCount,
                FollowingCount = data.followInfo.followingCount,
                FollowStatus = data.followInfo.followStatus,
            };
            user.FansClub = new FansClubInfo()
            {
                ClubName = "",
                Level = 0
            };

            if (data.fansClub != null && data.fansClub.Data != null)
            {
                user.FansClub.ClubName = data.fansClub.Data.clubName;
                user.FansClub.Level = data.fansClub.Data.Level;
            }

            return user;
        }

        static int count = 0;
        private void PrintMsg(Msg msg, PackMsgType barType)
        {
            console.WriteLine($"{DateTime.Now.ToString("HH:mm:ss")} [{barType}]", ConsoleColor.White);

            if (!Appsetting.PrintBarrage) return;
            if (Appsetting.Current.PrintFilter.Any() && !Appsetting.Current.PrintFilter.Contains(barType.GetHashCode())) return;

            var rinfo = AppRuntime.RoomCaches.GetCachedWebRoomInfo(msg.RoomId.ToString());
            var roomName = (rinfo?.Owner?.Nickname ?? ("直播间" + (msg.WebRoomId == 0 ? msg.RoomId : msg.WebRoomId)));
            var text = $"{DateTime.Now.ToString("HH:mm:ss")} [{roomName}] [{barType}]";

            if (msg.User != null)
            {
                text += $" [{msg.User?.GenderToString()}] ";
            }

            ConsoleColor color = ConsoleColor.White;
            var append = msg.Content;
            switch (barType)
            {
                case PackMsgType.弹幕消息: color = ConsoleColor.White; append = $"{msg?.User?.Nickname}: {msg.Content}"; break;
                case PackMsgType.点赞消息: color = ConsoleColor.Cyan; break;
                case PackMsgType.进直播间: color = ConsoleColor.Green; break;
                case PackMsgType.关注消息: color = ConsoleColor.Yellow; break;
                case PackMsgType.礼物消息: color = ConsoleColor.Red; break;
                case PackMsgType.直播间统计: color = ConsoleColor.Magenta; break;
                case PackMsgType.粉丝团消息: color = ConsoleColor.Blue; break;
                case PackMsgType.直播间分享: color = ConsoleColor.DarkBlue; break;
                case PackMsgType.下播: color = ConsoleColor.DarkCyan; append = $"直播已结束"; break;
                default: break;
            }
            text += append;

            if (++count > 10000)
            {
                Console.Clear();
                Console.WriteLine("控制台已清理");
                count = 0;
            }
            console.WriteLine(text + "\n", color);
        }

        //粉丝团
        private void Grab_OnFansclubMessage(object sender, WssBarrageGrab.RoomMessageEventArgs<FansclubMessage> e)
        {
            console.WriteLine($"粉丝团消息：{e.Message.Common.roomId} {e.Message.Content}", ConsoleColor.Blue);
            var msg = e.Message;
            if (!CheckRoomId(msg.Common.roomId)) return;
            var enty = new FansclubMsg()
            {
                MsgId = msg.Common.msgId,
                Content = msg.Content,
                RoomId = msg.Common.roomId,
                WebRoomId = AppRuntime.RoomCaches.GetCachedWebRoomid(msg.Common.roomId.ToString()),
                Type = msg.Type,
                User = GetUser(msg.User)
            };
            enty.Level = enty.User.FansClub.Level;
            
            var msgType = PackMsgType.粉丝团消息;
            PrintMsg(enty, msgType);
            Broadcast(new BarrageMsgPack(enty.ToJson(), msgType,e.Process));
        }

        //统计消息
        private void Grab_OnRoomUserSeqMessage(object sender, WssBarrageGrab.RoomMessageEventArgs<RoomUserSeqMessage> e)
        {
            console.WriteLine($"统计消息：{e.Message.Common.roomId} {e.Message.Total} {e.Message.totalUser}", ConsoleColor.Magenta);
            var msg = e.Message;
            if (!CheckRoomId(msg.Common.roomId)) return;
            var enty = new UserSeqMsg()
            {
                MsgId = msg.Common.msgId,
                OnlineUserCount = msg.Total,
                TotalUserCount = msg.totalUser,
                TotalUserCountStr = msg.totalPvForAnchor,
                OnlineUserCountStr = msg.onlineUserForAnchor,
                RoomId = msg.Common.roomId,
                WebRoomId = AppRuntime.RoomCaches.GetCachedWebRoomid(msg.Common.roomId.ToString()),
                Content = $"当前直播间人数 {msg.onlineUserForAnchor}，累计直播间人数 {msg.totalPvForAnchor}",
                User = null
            };
            
            var msgType = PackMsgType.直播间统计;
            PrintMsg(enty, msgType);
            var pack = new BarrageMsgPack(enty.ToJson(), msgType, e.Process);
            Broadcast(pack);
        }

        //礼物
        private void Grab_OnGiftMessage(object sender, WssBarrageGrab.RoomMessageEventArgs<GiftMessage> e)
        {
            console.WriteLine($"礼物消息：{e.Message.Common.roomId} {e.Message.giftId} {e.Message.repeatCount}", ConsoleColor.Red);
            var msg = e.Message;
            if (!CheckRoomId(msg.Common.roomId)) return;

            var key = msg.Common.roomId + "-" + msg.giftId + "-" + msg.groupId.ToString();

            //判断礼物重复
            if (msg.repeatEnd == 1)
            {
                //清除缓存中的key
                if (msg.groupId > 0 && giftCountCache.ContainsKey(key))
                {
                    lock (giftCountCache)
                    {
                        giftCountCache.Remove(key);
                    }
                }
                return;
            }

            int lastCount = 0;
            int currCount = (int)msg.repeatCount;
            var backward = currCount <= lastCount;
            if (currCount <= 0) currCount = 1;

            if (giftCountCache.ContainsKey(key))
            {
                lastCount = giftCountCache[key].Item1;
                backward = currCount <= lastCount;
                if (!backward)
                {
                    lock (giftCountCache)
                    {
                        giftCountCache[key] = Tuple.Create(currCount, DateTime.Now);
                    }
                }
            }
            else
            {
                if (msg.groupId > 0 && !backward)
                {
                    lock (giftCountCache)
                    {
                        giftCountCache.Add(key, Tuple.Create(currCount, DateTime.Now));
                    }
                }
            }
            //比上次小，则说明先后顺序出了问题，直接丢掉，应为比它大的消息已经处理过了
            if (backward) return;


            var count = currCount - lastCount;

            var enty = new GiftMsg()
            {
                MsgId = msg.Common.msgId,
                RoomId = msg.Common.roomId,
                WebRoomId = AppRuntime.RoomCaches.GetCachedWebRoomid(msg.Common.roomId.ToString()),
                Content = $"{msg.User.Nickname} 送出 {msg.Gift.Name} x {currCount} 个，增量{count}个",
                DiamondCount = msg.Gift.diamondCount,
                RepeatCount = currCount,
                GiftCount = count,
                GroupId = msg.groupId,
                GiftId = msg.giftId,
                GiftName = msg.Gift.Name,
                User = GetUser(msg.User)
            };
            
            var msgType = PackMsgType.礼物消息;
            PrintMsg(enty, msgType);
            var pack = new BarrageMsgPack(enty.ToJson(), PackMsgType.礼物消息, e.Process);
            Broadcast(pack);
        }

        //关注
        private void Grab_OnSocialMessage(object sender, WssBarrageGrab.RoomMessageEventArgs<SocialMessage> e)
        {
            console.WriteLine($"关注消息：{e.Message.Common.roomId} {e.Message.Action}", ConsoleColor.Yellow);

            var msg = e.Message;
            if (!CheckRoomId(msg.Common.roomId)) return;
            if (msg.Action != 1) return;
            var enty = new Msg()
            {
                MsgId = msg.Common.msgId,
                Content = $"{msg.User.Nickname} 关注了主播",
                RoomId = msg.Common.roomId,
                WebRoomId = AppRuntime.RoomCaches.GetCachedWebRoomid(msg.Common.roomId.ToString()),
                User = GetUser(msg.User)
            };
            
            var msgType = PackMsgType.关注消息;
            PrintMsg(enty, msgType);
            var pack = new BarrageMsgPack(enty.ToJson(), msgType, e.Process);
            var json = JsonConvert.SerializeObject(pack);
            Broadcast(pack);
        }

        //直播间分享
        private void Grab_OnShardMessage(object sender, WssBarrageGrab.RoomMessageEventArgs<SocialMessage> e)
        {
            console.WriteLine($"分享消息：{e.Message.Common.roomId} {e.Message.shareTarget}", ConsoleColor.DarkBlue);

            var msg = e.Message;
            if (!CheckRoomId(msg.Common.roomId)) return;
            if (msg.Action != 3) return;
            ShareType type = ShareType.未知;
            if (Enum.IsDefined(type.GetType(), int.Parse(msg.shareTarget)))
            {
                type = (ShareType)int.Parse(msg.shareTarget);
            }

            var enty = new ShareMessage()
            {
                MsgId = msg.Common.msgId,
                Content = $"{msg.User.Nickname} 分享了直播间到{type}",
                RoomId = msg.Common.roomId,
                WebRoomId = AppRuntime.RoomCaches.GetCachedWebRoomid(msg.Common.roomId.ToString()),
                ShareType = type,
                User = GetUser(msg.User)
            };
            
            var msgType = PackMsgType.直播间分享;
            PrintMsg(enty, msgType);

            //shareTarget: (112:好友),(1微信)(2朋友圈)(3微博)(5:qq)(4:qq空间),shareType: 1            
            var pack = new BarrageMsgPack(enty.ToJson(), msgType, e.Process);
            var json = JsonConvert.SerializeObject(pack);
            Broadcast(pack);
        }

        //来了
        private void Grab_OnMemberMessage(object sender, WssBarrageGrab.RoomMessageEventArgs<Modles.ProtoEntity.MemberMessage> e)
        {
            console.WriteLine($"进入直播间：{e.Message.Common.roomId} {e.Message.memberCount}", ConsoleColor.Green);

            var msg = e.Message;
            if (!CheckRoomId(msg.Common.roomId)) return;

            var enty = new Modles.JsonEntity.MemberMessage()
            {
                MsgId = msg.Common.msgId,
                Content = $"{msg.User.Nickname} 来了 直播间人数:{msg.memberCount}",
                RoomId = msg.Common.roomId,
                WebRoomId = AppRuntime.RoomCaches.GetCachedWebRoomid(msg.Common.roomId.ToString()),
                CurrentCount = msg.memberCount,
                User = GetUser(msg.User)
            };
            
            var msgType = PackMsgType.进直播间;
            PrintMsg(enty, msgType);
            var pack = new BarrageMsgPack(enty.ToJson(), msgType, e.Process);
            var json = JsonConvert.SerializeObject(pack);
            Broadcast(pack);
        }

        //点赞
        private void Grab_OnLikeMessage(object sender, WssBarrageGrab.RoomMessageEventArgs<LikeMessage> e)
        {
            console.WriteLine($"点赞消息：{e.Message.Common.roomId} {e.Message.Count}", ConsoleColor.Cyan);

            var msg = e.Message;
            if (!CheckRoomId(msg.Common.roomId)) return;

            var enty = new LikeMsg()
            {
                MsgId = msg.Common.msgId,
                Count = msg.Count,
                Content = $"{msg.User.Nickname} 为主播点了{msg.Count}个赞，总点赞{msg.Total}",
                RoomId = msg.Common.roomId,
                WebRoomId = AppRuntime.RoomCaches.GetCachedWebRoomid(msg.Common.roomId.ToString()),
                Total = msg.Total,
                User = GetUser(msg.User)
            };
            
            var msgType = PackMsgType.点赞消息;
            PrintMsg(enty, msgType);
            var pack = new BarrageMsgPack(enty.ToJson(), msgType, e.Process);
            Broadcast(pack);
        }

        //弹幕
        private void Grab_OnChatMessage(object sender, WssBarrageGrab.RoomMessageEventArgs<ChatMessage> e)
        {
            console.WriteLine($"弹幕消息：{e.Message.Common.roomId} {e.Message.Content}", ConsoleColor.White);

            var msg = e.Message;
            if (!CheckRoomId(msg.Common.roomId)) return;

            var enty = new Msg()
            {
                MsgId = msg.Common.msgId,
                Content = msg.Content,
                RoomId = msg.Common.roomId,
                WebRoomId = AppRuntime.RoomCaches.GetCachedWebRoomid(msg.Common.roomId.ToString()),
                User = GetUser(msg.User),
            };
            
            var msgType = PackMsgType.弹幕消息;
            PrintMsg(enty, msgType);

            var pack = new BarrageMsgPack(enty.ToJson(), msgType, e.Process);
            Broadcast(pack);
        }

        //直播间状态变更
        private void Grab_OnControlMessage(object sender, WssBarrageGrab.RoomMessageEventArgs<ControlMessage> e)
        {
            console.WriteLine($"直播间状态变更：{e.Message.Common.roomId} {e.Message.Status}", ConsoleColor.Yellow);
            var msg = e.Message;
            if (!CheckRoomId(msg.Common.roomId)) return;
            BarrageMsgPack pack = null;
            //下播
            if (msg.Status == 3)
            {
                var enty = new Msg()
                {
                    MsgId = msg.Common.msgId,
                    Content = "直播已结束",
                    RoomId = msg.Common.roomId,
                    WebRoomId = AppRuntime.RoomCaches.GetCachedWebRoomid(msg.Common.roomId.ToString()),
                    User = null
                };
                
                var msgType = PackMsgType.下播;
                PrintMsg(enty, msgType);
                pack = new BarrageMsgPack(enty.ToJson(), PackMsgType.下播, e.Process);
            }

            if (pack != null)
            {
                Broadcast(pack);
            }
        }

        //static int count = 0;
        //private void Print(string msg, ConsoleColor color, PackMsgType bartype)
        //{
        //    if (!Appsetting.PrintFilter.Any(a => a == bartype.GetHashCode())) return;
        //    if (Appsetting.PrintBarrage)
        //    {
        //        if (++count > 1000)
        //        {
        //            Console.Clear();
        //            Console.WriteLine("控制台已清理");
        //        }
        //        console.WriteLine($"{DateTime.Now.ToString("HH:mm:ss")} [{bartype.ToString()}] " + msg + "\n", color);
        //        count = 0;
        //    }
        //}

        private void Dieout_Elapsed(object sender, ElapsedEventArgs e)
        {
            console.WriteLine($"Dieout_Elapsed", ConsoleColor.Green);

            var now = DateTime.Now;
            var dieoutKvs = socketList.Where(w => w.Value.LastPing.AddSeconds(dieout.Interval * 3) < now).ToList();
            foreach (var kv in dieoutKvs)
            {
                try
                {
                    if (kv.Value == null || !kv.Value.Socket.IsAvailable) continue;
                    kv.Value.Socket.Close();
                }
                catch (SocketException ex)
                {
                    Console.WriteLine($"Failed to close socket: {ex.Message}", ConsoleColor.Red);
                    // Log the error details for further investigation
                    Console.WriteLine($"SocketException ErrorCode: {ex.ErrorCode}");
                    Console.WriteLine($"SocketException SocketErrorCode: {ex.SocketErrorCode}");
                }
                catch (Exception ex)
                {
                    // Catch any other types of exceptions
                    Console.WriteLine($"An unexpected error occurred: {ex.Message}", ConsoleColor.Red);
                }
            }

            //dieoutKvs.ForEach(f => f.Value.Socket.Close());
        }

        private void Listen(IWebSocketConnection socket)
        {
            console.WriteLine($"Listen", ConsoleColor.Green);

            //客户端url
            string clientUrl = socket.ConnectionInfo.ClientIpAddress + ":" + socket.ConnectionInfo.ClientPort;
            if (!socketList.ContainsKey(clientUrl))
            {
                socketList.Add(clientUrl, new UserState(socket));
                console.WriteLine($"{DateTime.Now.ToLongTimeString()} 已经建立与[{clientUrl}]的连接", ConsoleColor.Green);
            }
            else
            {
                socketList[clientUrl].Socket = socket;
            }

            //接收指令
            socket.OnMessage = (message) =>
            {
                try
                {
                    var cmdPack = JsonConvert.DeserializeObject<Command>(message);
                    if (cmdPack == null) return;

                    if (cmdPack.Cmd == CommandCode.Close)
                    {
                        console.WriteLine($"{DateTime.Now.ToLongTimeString()} 已经关闭与[{clientUrl}]的连接", ConsoleColor.Red);
                        this.Close();
                    }
                }
                catch (Exception) {
                    console.WriteLine($"接收到无法解析的指令：{message}", ConsoleColor.Red);
                }
            };

            socket.OnClose = () =>
            {
                console.WriteLine($"{DateTime.Now.ToLongTimeString()} 已经关闭与[{clientUrl}]的连接", ConsoleColor.Red);
                socketList.Remove(clientUrl);
            };

            socket.OnPing = (data) =>
            {
                console.WriteLine($"{DateTime.Now.ToLongTimeString()} 收到[{clientUrl}]的心跳包", ConsoleColor.Green);
                socketList[clientUrl].LastPing = DateTime.Now;
                socket.SendPong(Encoding.UTF8.GetBytes("pong"));
            };
        }

        /// <summary>
        /// 广播消息
        /// </summary>
        /// <param name="msg"></param>
        public void Broadcast(BarrageMsgPack pack)
        {
            console.WriteLine($"Broadcast", ConsoleColor.Green);
            if (pack == null) return;
            var offLines = new List<string>();
            foreach (var item in socketList)
            {
                var state = item.Value;
                if (item.Value.Socket.IsAvailable)
                {                   
                    state.Socket.Send(pack.ToJson());
                }
                else
                {
                    offLines.Add(item.Key);
                }
            }
            //删除掉线的套接字        
            offLines.ForEach(key => socketList.Remove(key));
        }

        /// <summary>
        /// 开始监听
        /// </summary>
        public void StartListen()
        {
            this.grab.Start(); //启动代理
            this.socketServer.Start(Listen);//启动监听
            console.WriteLine($"{this.socketServer.Location} 弹幕服务已启动...", ConsoleColor.Green);
            Console.Title = $"抖音弹幕监听推送 [{this.socketServer.Location}]";
        }

        /// <summary>
        /// 关闭服务器连接，并关闭系统代理
        /// </summary>
        public void Close()
        {
            console.WriteLine($"Close", ConsoleColor.Green);

            socketList.Values.ToList().ForEach(f => f.Socket.Close());
            socketList.Clear();
            socketServer.Dispose();
            grab.Dispose();

            this.OnClose?.Invoke(this, EventArgs.Empty);
        }

        class UserState
        {
            /// <summary>
            /// 套接字
            /// </summary>
            public IWebSocketConnection Socket { get; set; }

            /// <summary>
            /// 上次发起心跳包时间
            /// </summary>
            public DateTime LastPing { get; set; } = DateTime.Now;

            public UserState()
            {

            }
            public UserState(IWebSocketConnection socket)
            {
                Socket = socket;
            }
        }
    }
}
