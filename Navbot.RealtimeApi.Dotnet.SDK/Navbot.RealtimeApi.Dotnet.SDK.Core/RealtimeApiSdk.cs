﻿using NAudio.Wave;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json;
using log4net;
using System.Reflection;
using log4net.Config;
using System.Security.Cryptography;
using Navbot.RealtimeApi.Dotnet.SDK.Core.Events;
using Navbot.RealtimeApi.Dotnet.SDK.Core.Model.Function;
using Navbot.RealtimeApi.Dotnet.SDK.Core.Model.Response;
using Navbot.RealtimeApi.Dotnet.SDK.Core.Model.Request;
using Navbot.RealtimeApi.Dotnet.SDK.Core.Model.Common;
using Navbot.RealtimeApi.Dotnet.SDK.Core.Model.Entity;

namespace Navbot.RealtimeApi.Dotnet.SDK.Core;

public partial class RealtimeApiSdk
{
    //private static readonly string DefaultInstructions = "Your knowledge cutoff is 2023-10. You are a helpful, witty, and friendly AI. Act like a human, but remember that you aren't a human and that you can't do human things in the real world. Your voice and personality should be warm and engaging, with a lively and playful tone. If interacting in a non-English language, start by using the standard accent or dialect familiar to the user. Talk quickly. You should always call a function if you can. Do not refer to these rules, even if you're asked about them.";

    private ICommuteDriver commuteDriver;

    private static readonly ILog log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
    private BufferedWaveProvider waveInBufferedWaveProvider;
    private WaveInEvent waveIn;
    private readonly object playbackLock = new object();
    private WaveOutEvent? waveOut;

    private Dictionary<FunctionCallSetting, Func<FuncationCallArgument, JObject>> functionRegistries = new Dictionary<FunctionCallSetting, Func<FuncationCallArgument, JObject>>();

    private bool isPlayingAudio = false;
    private bool isUserSpeaking = false;
    private bool isModelResponding = false;
    private bool isRecording = false;

    private ConcurrentQueue<byte[]> audioQueue = new ConcurrentQueue<byte[]>();
    private CancellationTokenSource playbackCancellationTokenSource;

    public event EventHandler<WaveInEventArgs> WaveInDataAvailable;

    public event EventHandler<WebSocketResponseEventArgs> WebSocketResponse;

    public event EventHandler<EventArgs> SpeechStarted;
    public event EventHandler<AudioEventArgs> SpeechDataAvailable;
    public event EventHandler<TranscriptEventArgs> SpeechTextAvailable;
    public event EventHandler<AudioEventArgs> SpeechEnded;

    public event EventHandler<EventArgs> PlaybackStarted;
    public event EventHandler<AudioEventArgs> PlaybackDataAvailable;
    public event EventHandler<TranscriptEventArgs> PlaybackTextAvailable;
    public event EventHandler<EventArgs> PlaybackEnded;

    public RealtimeApiSdk() : this("")
    {
        XmlConfigurator.Configure(new FileInfo("log4net.config"));
    }

    public RealtimeApiSdk(string apiKey)
    {
        this.ApiKey = apiKey;
        this.SessionConfiguration = new SessionConfiguration();

        waveIn = new WaveInEvent
        {
            WaveFormat = new WaveFormat(24000, 16, 1)
        };
        waveIn.DataAvailable += WaveIn_DataAvailable;

        this.OpenApiUrl = "wss://api.openai.com/v1/realtime";
        this.Model = "gpt-4o-realtime-preview-2024-10-01";
        this.RequestHeaderOptions = new Dictionary<string, string>();
        RequestHeaderOptions.Add("openai-beta", "realtime=v1");
    }

    public string ApiKey { get; set; }

    public bool IsRunning { get; private set; }

    public string OpenApiUrl { get; set; }

    public string Model { get; set; }

    //public string CustomInstructions { get; set; }
    public NetworkDriverType NetworkDriverType { get; set; }

    public bool IsMuted { get; set; } = false;

    public SessionConfiguration SessionConfiguration { get; }

    public Dictionary<string, string> RequestHeaderOptions { get; }

    protected virtual void OnWaveInDataAvailable(WaveInEventArgs e)
    {
        WaveInDataAvailable?.Invoke(this, e);
    }
    protected virtual void OnWebSocketResponse(WebSocketResponseEventArgs e)
    {
        WebSocketResponse?.Invoke(this, e);
    }
    protected virtual void OnSpeechStarted(EventArgs e)
    {
        SpeechStarted?.Invoke(this, e);
    }
    protected virtual void OnSpeechEnded(AudioEventArgs e)
    {
        SpeechEnded?.Invoke(this, e);
    }
    protected virtual void OnPlaybackDataAvailable(AudioEventArgs e)
    {
        PlaybackDataAvailable?.Invoke(this, e);
    }
    protected virtual void OnSpeechDataAvailable(AudioEventArgs e)
    {
        SpeechDataAvailable?.Invoke(this, e);
    }
    protected virtual void OnSpeechActivity(bool isActive, AudioEventArgs? audioArgs = null)
    {
        if (isActive)
        {
            SpeechStarted?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            SpeechEnded?.Invoke(this, audioArgs ?? new AudioEventArgs(new byte[0]));
        }
    }
    protected virtual void OnPlaybackStarted(EventArgs e)
    {
        PlaybackStarted?.Invoke(this, e);
    }
    protected virtual void OnPlaybackEnded(EventArgs e)
    {
        PlaybackEnded?.Invoke(this, e);
    }
    public async void StartSpeechRecognitionAsync()
    {
        if (!IsRunning)
        {
            IsRunning = true;

            commuteDriver = GetCommuteDriver();

            await InitializeWebSocketAsync();
            InitalizeWaveProvider();

            await commuteDriver.SetDataReceivedCallback(OnDataReceivedAsync);

            var sendAudioTask = StartAudioRecordingAsync();
            var receiveTask = commuteDriver.ReceiveMessages();

            await Task.WhenAll(sendAudioTask, receiveTask);
        }
    }
    public async void StopSpeechRecognitionAsync()
    {
        if (IsRunning)
        {
            StopAudioRecording();

            StopAudioPlayback();

            ClearAudioQueue();

            await CommitAudioBufferAsync();
            await CloseWebSocketAsync();

            isPlayingAudio = false;
            isUserSpeaking = false;
            isModelResponding = false;
            isRecording = false;

            IsRunning = false;
        }
    }

    public void RegisterFunctionCall(FunctionCallSetting functionCallSetting, Func<FuncationCallArgument, JObject> functionCallback)
    {
        functionRegistries.Add(functionCallSetting, functionCallback);
    }

    private void ValidateApiKey()
    {
        if (string.IsNullOrEmpty(ApiKey))
        {
            throw new InvalidOperationException("Invalid API Key.");
        }
    }
    private string GetAuthorization()
    {
        string authorization = ApiKey;
        if (!ApiKey.StartsWith("Bearer ", StringComparison.InvariantCultureIgnoreCase))
        {
            authorization = $"Bearer {ApiKey}";
        }

        return authorization;
    }
    private void InitalizeWaveProvider()
    {
        waveInBufferedWaveProvider = new BufferedWaveProvider(new WaveFormat(24000, 16, 1))
        {
            BufferDuration = TimeSpan.FromSeconds(100),
            DiscardOnBufferOverflow = true
        };

    }
    private async Task InitializeWebSocketAsync()
    {
        await commuteDriver.ConnectAsync(RequestHeaderOptions, GetAuthorization(), GetOpenAIRequestUrl());
    }
    private async Task CloseWebSocketAsync()
    {
        await commuteDriver.DisconnectAsync();
    }

    private async void WaveIn_DataAvailable(object? sender, WaveInEventArgs e)
    {
        if (IsMuted) return;

        string base64Audio = Convert.ToBase64String(e.Buffer, 0, e.BytesRecorded);
        var audioMessage = new JObject
        {
            ["type"] = "input_audio_buffer.append",
            ["audio"] = base64Audio
        };

        var messageBytes = Encoding.UTF8.GetBytes(audioMessage.ToString());
        await commuteDriver.SendDataAsync(messageBytes);


        OnSpeechDataAvailable(new AudioEventArgs(e.Buffer));
        OnWaveInDataAvailable(new WaveInEventArgs(e.Buffer, e.BytesRecorded));
    }

    private async Task StartAudioRecordingAsync()
    {
        waveIn.StartRecording();
        isRecording = true;

        OnSpeechStarted(new EventArgs());

        log.Info("Audio recording started.");
    }
    private void StopAudioRecording()
    {
        if (waveIn != null && isRecording)
        {
            waveIn.StopRecording();

            isRecording = false;
            log.Debug("Recording stopped to prevent echo.");
        }
    }
    private void StopAudioPlayback()
    {
        log.Debug("StopAudioPlayback() called...");
        if (playbackCancellationTokenSource != null && !playbackCancellationTokenSource.IsCancellationRequested)
        {
            playbackCancellationTokenSource.Cancel();
            log.Info("AI audio playback stopped due to user interruption.");
        }

        if (waveOut != null)
        {
            try
            {
                waveOut.Stop();
                waveOut.Dispose();
            }
            catch (Exception ex)
            {
                log.Error($"Error stopping waveOut: {ex.Message}");
            }
            finally
            {
                waveOut = null; // Clear reference so we can re-init next time
            }
        }
        // 3) Clear out any leftover audio in the buffer
        waveInBufferedWaveProvider?.ClearBuffer();

        // 4) Indicate playback ended in the logs/events
        log.Info("AI audio playback force-stopped due to user interruption.");
        OnPlaybackEnded(EventArgs.Empty);
    }

    private void StartAudioPlayback()
    {
        waveOut.Play();
    }

    private void ClearBufferedWaveProvider()
    {
        lock (playbackLock)
        {
            if (waveInBufferedWaveProvider != null)
            {
                waveInBufferedWaveProvider.ClearBuffer();
                log.Info("BufferedWaveProvider buffer cleared.");
            }
        }
    }

    private async Task CommitAudioBufferAsync()
    {
        await commuteDriver.CommitAudioBufferAsync();
    }
    private void ClearAudioQueue()
    {
        lock (playbackLock)
        {
            while (audioQueue.TryDequeue(out _)) { }
            log.Info("Audio queue cleared.");
        }
    }

    private async Task OnDataReceivedAsync(string data)
    {
        JObject json= JObject.Parse(data);
        await HandleWebSocketMessage(json);
    }

    private async Task HandleWebSocketMessage(JObject json)
    {
        try
        {
            var type = json["type"]?.ToString();
            log.Info($"Received type: {type}");

            BaseResponse baseResponse = BaseResponse.Parse(json);
            await HandleBaseResponse(baseResponse, json);

            //OnWebSocketResponse(new WebSocketResponseEventArgs(baseResponse, webSocketClient));
        }
        catch (Exception e)
        {
            log.Error(e.Message);
        }
    }

    private async Task HandleBaseResponse(BaseResponse baseResponse, JObject json)
    {
        switch (baseResponse)
        {
            case SessionCreated:
                log.Info($"Received json: {json}");
                SendSessionUpdate();
                break;

            case Core.Model.Response.SessionUpdate sessionUpdate:
                log.Info($"Received json: {json}");
                if (!isRecording)
                    await StartAudioRecordingAsync();
                break;

            case Core.Model.Response.SpeechStarted:
                log.Info($"Received json: {json}");
                HandleUserSpeechStarted();
                break;

            case SpeechStopped:
                log.Info($"Received json: {json}");
                HandleUserSpeechStopped();
                break;

            case ResponseDelta responseDelta:
                await HandleResponseDelta(responseDelta);
                break;

            case TranscriptionCompleted transcriptionCompleted:
                log.Info($"Received json: {json}");
                OnSpeechTextAvailable(new TranscriptEventArgs(transcriptionCompleted.Transcript));
                break;

            case ResponseAudioTranscriptDone textDone:
                log.Info($"Received json: {json}");
                OnPlaybackTextAvailable(new TranscriptEventArgs(textDone.Transcript));
                break;

            case FuncationCallArgument argument:
                log.Info($"Received json: {json}");
                HandleFunctionCall(argument);
                break;

            case ConversationItemCreated:
            case BufferCommitted:
            case ResponseCreated:
            case ConversationCreated:
            case TranscriptionFailed:
            case ConversationItemTruncate:
            case ConversationItemDeleted:
            case BufferClear:
            case ResponseDone:
            case ResponseOutputItemAdded:
            case ResponseOutputItemDone:
            case ResponseContentPartAdded:
            case ResponseContentPartDone:
            case ResponseTextDone:
            case ResponseFunctionCallArgumentsDelta:
            case RateLimitsUpdated:
                log.Info($"Received json: {json}");
                break;

            case ResponseError error:
                log.Error(error);
                log.Error($"Received json: {json}");
                break;
        }
    }

    private async Task HandleResponseDelta(ResponseDelta responseDelta)
    {
        switch (responseDelta.ResponseDeltaType)
        {
            case ResponseDeltaType.AudioTranscriptDelta:
                // Handle AudioTranscriptDelta if necessary
                log.Info($"Received json: {responseDelta}");
                break;

            case ResponseDeltaType.AudioDelta:
                log.Debug($"Received json: {responseDelta}");
                ProcessAudioDelta(responseDelta);
                break;

            case ResponseDeltaType.AudioDone:
                log.Info($"Received json: {responseDelta}");
                isModelResponding = false;
                ResumeRecording();
                break;
            case ResponseDeltaType.TextDelta:
                log.Info($"Received json: {responseDelta}");
                break;
        }
    }

    // Websocket  - > RealtimeApiSdk.WebSocket.cs

    //TODO MOve all function realted to RealtimeApiSdk.FunctionCall.cs
    private void HandleFunctionCall(FuncationCallArgument argument)
    {
        string functionName = argument.Name;
        foreach (var item in functionRegistries)
        {
            if (item.Key.Name == functionName)
            {
                JObject functionCallResultJson = item.Value(argument);
                var callId = argument.CallId;
                SendFunctionCallResult(functionCallResultJson, callId);
            }
        }
    }

    private void SendFunctionCallResult(JObject functionCallResultJson, string callId)
    {
        string outputStr = functionCallResultJson == null ? "" : functionCallResultJson.ToString();
        var functionCallResult = new FunctionCallResult
        {
            Type = "conversation.item.create",
            Item = new FunctionCallItem
            {
                Type = "function_call_output",
                Output = outputStr,
                CallId = callId
            }
        };

        string resultJsonString = JsonConvert.SerializeObject(functionCallResult);

        commuteDriver.SendDataAsync(Encoding.UTF8.GetBytes(resultJsonString)).Wait();
        Console.WriteLine("Sent function call result: " + resultJsonString);

        ResponseCreate responseJson = new ResponseCreate();
        string rpJsonString = JsonConvert.SerializeObject(responseJson);

        commuteDriver.SendDataAsync(Encoding.UTF8.GetBytes(rpJsonString)).Wait();
    }

    // Add SessionConfiguration property
    // Add SessionConfiguration class
    //instructions = string.IsNullOrWhiteSpace(CustomInstructions) ? DefaultInstructions : CustomInstructions,
    //            turn_detection = new Model.Request.TurnDetection
    //            {
    //                type = "server_vad",
    //                threshold = 0.6,
    //                prefix_padding_ms = 300,
    //                silence_duration_ms = 500
    //            },
    //            voice = "alloy",
    //            temperature = 1,
    //            max_response_output_tokens = 4096,
    //            modalities = new List<string> { "text", "audio" },
    //            input_audio_format = "pcm16",
    //            output_audio_format = "pcm16",
    //            input_audio_transcription = new Model.Request.AudioTranscription { model = "whisper-1" },
    //            tool_choice = "auto",
    private async void SendSessionUpdate()
    {
        // TODO consider set functioncall into SessionConfiguration .
        JArray functionSettings = new JArray();
        foreach (var item in functionRegistries)
        {
            string jsonString = JsonConvert.SerializeObject(item.Key);
            JObject jObject = JObject.Parse(jsonString);
            functionSettings.Add(jObject);
        }

        var sessionUpdateRequest = new Model.Request.SessionUpdate
        {
            session = this.SessionConfiguration.ToSession(),
            //    session = new Model.Request.Session
            //    {
            //        instructions = string.IsNullOrWhiteSpace(CustomInstructions) ? DefaultInstructions : CustomInstructions,
            //        turn_detection = new Model.Request.TurnDetection
            //        {
            //            type = "server_vad",
            //            threshold = 0.6,
            //            prefix_padding_ms = 300,
            //            silence_duration_ms = 500
            //        },
            //        voice = "alloy",
            //        temperature = 1,
            //        max_response_output_tokens = 4096,
            //        modalities = new List<string> { "text", "audio" },
            //        input_audio_format = "pcm16",
            //        output_audio_format = "pcm16",
            //        input_audio_transcription = new Model.Request.AudioTranscription { model = "whisper-1" },
            //        tool_choice = "auto",
            //        tools = functionSettings
            //    }
        };
        sessionUpdateRequest.session.tools = functionSettings;

        string message = JsonConvert.SerializeObject(sessionUpdateRequest);
        await commuteDriver.SendDataAsync(Encoding.UTF8.GetBytes(message));
        log.Debug("Sent session update: " + message);
    }
    private void HandleUserSpeechStarted()
    {
        // 1) Stop playback *before* setting isModelResponding = false
        isUserSpeaking = true;
        log.Debug("User started speaking.");
        StopAudioPlayback();
        // 2) Now set isModelResponding = false after we already canceled playback
        isModelResponding = false;

        ClearAudioQueue();

        OnSpeechStarted(new EventArgs());
        OnSpeechActivity(true);
    }
    private void HandleUserSpeechStopped()
    {
        isUserSpeaking = false;
        log.Debug("User stopped speaking. Processing audio queue...");
        ProcessAudioQueue();

        OnSpeechActivity(false, new AudioEventArgs(new byte[0]));
    }
    private void ProcessAudioDelta(ResponseDelta responseDelta)
    {
        if (isUserSpeaking) return;

        var base64Audio = responseDelta.Delta;
        if (!string.IsNullOrEmpty(base64Audio))
        {
            var audioBytes = Convert.FromBase64String(base64Audio);
            audioQueue.Enqueue(audioBytes);
            isModelResponding = true;

            OnPlaybackDataAvailable(new AudioEventArgs(audioBytes));
            StopAudioRecording();
            OnSpeechActivity(true);
        }
    }
    private void ResumeRecording()
    {
        if (waveIn != null && !isRecording && !isModelResponding)
        {
            waveIn.StartRecording();
            isRecording = true;
            log.Debug("Recording resumed after audio playback.");
            OnSpeechStarted(new EventArgs());
            OnSpeechActivity(true);
        }
    }
    private void ProcessAudioQueue()
    {
        if (!isPlayingAudio)
        {
            isPlayingAudio = true;
            audioQueue = new ConcurrentQueue<byte[]>();
            playbackCancellationTokenSource = new CancellationTokenSource();

            Task.Run(() =>
            {
                try
                {
                    OnPlaybackStarted(new EventArgs());

                    using var waveOut = new WaveOutEvent { DesiredLatency = 200 };
                    // 1) Create and store waveOut so we can stop it later

                    waveOut.PlaybackStopped += (s, e) => { OnPlaybackEnded(new EventArgs()); };
                    waveOut.Init(waveInBufferedWaveProvider);
                    waveOut.Play();

                    while (!playbackCancellationTokenSource.Token.IsCancellationRequested)
                    {
                        if (audioQueue.TryDequeue(out var audioData))
                        {
                            waveInBufferedWaveProvider.AddSamples(audioData, 0, audioData.Length);

                            //float[] waveform = ExtractWaveform(audioData);
                        }
                        else
                        {
                            log.Debug("No audio in queue; waiting...");
                            Task.Delay(100).Wait();
                        }
                    }

                    log.Debug("Playback loop exited; calling waveOut.Stop()");
                    if (waveOut != null)
                    {
                        waveOut.Stop();
                    }
                }
                catch (Exception ex)
                {
                    log.Error($"Error during audio playback: {ex.Message}");
                }
                finally
                {
                    isPlayingAudio = false;
                }
            });
        }
    }
    private string GetOpenAIRequestUrl()
    {
        string url = $"{this.OpenApiUrl.TrimEnd('/').TrimEnd('?')}?model={this.Model}";
        return url;
    }

    private ICommuteDriver GetCommuteDriver()
    {
        ICommuteDriver rtn = null;

        //NetworkDriverType = NetworkDriverType.WebRTC;
        switch (NetworkDriverType)
        {
            case NetworkDriverType.WebSocket:
                rtn = new WebSocketCommuteDriver(log);
                break;
            case NetworkDriverType.WebRTC:
                rtn = new WebRTCCommuteDriver(log);
                break;
            default:
                break;
        }

        return rtn;
    }
}
