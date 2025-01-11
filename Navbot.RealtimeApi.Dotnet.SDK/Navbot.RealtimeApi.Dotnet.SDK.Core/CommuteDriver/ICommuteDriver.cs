﻿using log4net;
using Navbot.RealtimeApi.Dotnet.SDK.Core.Events;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Navbot.RealtimeApi.Dotnet.SDK.Core
{
    interface ICommuteDriver
    {
        event EventHandler<DataReceivedEventArgs> ReceivedDataAvailable;

        Task ConnectAsync(Dictionary<string, string> RequestHeaderOptions, string authorization, string url);

        Task DisconnectAsync();

        Task SendDataAsync(byte[]? messageBytes);

        Task CommitAudioBufferAsync();

        Task ReceiveMessages();

        Task SetDataReceivedCallback(Func<string, Task> callback);
    }
}
