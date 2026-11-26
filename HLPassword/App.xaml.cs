using System.Configuration;
using System.Data;
using System.Windows;

namespace HLPassword
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        public static SupabaseConfiguration Supabase { get; } = SupabaseConfiguration.LoadFromEnvironment();
    }

}
