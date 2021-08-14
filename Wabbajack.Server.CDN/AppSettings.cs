using Microsoft.Extensions.Configuration;

namespace Wabbajack.Server.CDN
{
    public class AppSettings
    {
        public AppSettings(IConfiguration config)
        {
            config.Bind("WabbajackSettings", this);
        }
        
        public string Remote { get; set; }
        public string CDNFolder { get; set; }
        public string AuthFile { get; set; }
    }
}
