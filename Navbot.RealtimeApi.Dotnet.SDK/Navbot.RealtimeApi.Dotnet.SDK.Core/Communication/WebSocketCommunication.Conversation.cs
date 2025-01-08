﻿using Navbot.RealtimeApi.Dotnet.SDK.Core.Events;
using Navbot.RealtimeApi.Dotnet.SDK.Core.Model.Common;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Navbot.RealtimeApi.Dotnet.SDK.Core.Communication
{
    public partial class WebSocketCommunication : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        private readonly List<ConversationEntry> conversationEntries = new List<ConversationEntry>();
        private readonly StringBuilder conversationTextBuilder = new StringBuilder();
        public IReadOnlyList<ConversationEntry> ConversationEntries => conversationEntries.AsReadOnly();
        public string ConversationAsText => conversationTextBuilder.ToString();

        public void ClearConversationEntries()
        {
            conversationEntries.Clear();
            conversationTextBuilder.Clear();
        }

        private void AddConversationEntry(string source, string content)
        {
            var entry = new ConversationEntry
            {
                UTCTimestamp = DateTime.UtcNow,
                Source = source,
                Content = content
            };

            conversationEntries.Add(entry);
            conversationTextBuilder.AppendLine($"{entry.UTCTimestamp:HH:mm:ss} [{entry.Source}] {entry.Content}");

            NotifyConversationAsTextChanged();
        }

        protected virtual void OnSpeechTextAvailable(TranscriptEventArgs e)
        {
            AddConversationEntry("User", e.Transcript);
            SpeechTextAvailable?.Invoke(this, e);
        }

        protected virtual void OnPlaybackTextAvailable(TranscriptEventArgs e)
        {
            AddConversationEntry("AI", e.Transcript);
            PlaybackTextAvailable?.Invoke(this, e);
        }
        protected virtual void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        private void NotifyConversationAsTextChanged()
        {
            OnPropertyChanged(nameof(ConversationAsText));
        }
    }
}
