using Navbot.RealtimeApi.Dotnet.SDK.Core.Enum;
using Navbot.RealtimeApi.Dotnet.SDK.Core;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;

namespace WPFDemos
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
            DataContext = this;
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            string openAiApiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? "";

            myRealtimeControlFull.OpenAiApiKey = openAiApiKey;
            myRealtimeControlFull.NetworkProtocolType = NetworkProtocolType.WebSocket;
            myRealtimeControlFull.VoiceVisualEffect = VoiceVisualEffect.SoundWave;
            myRealtimeControlFull.ShowChatTranscript = true;
            myRealtimeControlFull.ShowButtonBar = true;
        }
    }
}