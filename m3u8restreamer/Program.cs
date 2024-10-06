using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using EmbedIO;
using EmbedIO.Actions;
using Swan.Logging;
using System.Net.Http;
using System.Collections.Generic;
using System.IO;

namespace m3u8restreamer
{
    public class Program
    {
        private static void Main()
        {
            var pipProcess = Process.Start(new ProcessStartInfo
            {
                FileName = "pip3",
                Arguments = "install --upgrade yt-dlp",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            });

            pipProcess?.StandardOutput.ReadToEnd().Log(nameof(Main), LogLevel.Info);
            
            StartServer();
            Thread.Sleep(Timeout.Infinite);
        }

        private static void StartServer()
        {
            string server = $"http://+:{11034}";

            $"Starting server on {server}".Log(nameof(StartServer), LogLevel.Info);

            WebServer webServer = new WebServer(o => o
                    .WithUrlPrefix(server)
                    .WithMode(HttpListenerMode.EmbedIO))
                .WithLocalSessionManager()
                .WithModule(new ActionModule("/getStream", HttpVerbs.Any, GetStream))
                .WithModule(new ActionModule("/convertM3U", HttpVerbs.Any, ConvertM3U)); 

            // Important: Do not ignore write exceptions, but let them bubble up.
            // This allows us to see when a client disconnects, so that we can stop streaming.
            // (Otherwise we could stream to a disconnected client indefinitely.)
            webServer.Listener.IgnoreWriteExceptions = false;

            webServer.RunAsync();

            "Server is started and ready to receive connections.".Log(nameof(StartServer), LogLevel.Info);
        }

        private static async Task ConvertM3U(IHttpContext context)
        {
            // Extract the full URL
            string fullUrl = context.Request.Url.ToString();
            Uri uri = new Uri(fullUrl);
        
            // Save baseURL
            string baseURL = uri.GetLeftPart(UriPartial.Authority) + "/getStream/";
        
            // Save queryString
            string queryString = uri.Query;
        
            // Extract m3u URL
            string m3uPath = uri.AbsolutePath.Replace("/convertM3U/", "");
            string m3u = HttpUtility.UrlDecode(m3uPath);
        
            // Download m3u file content
            string m3uContent;
            using (HttpClient client = new HttpClient())
            {
                m3uContent = await client.GetStringAsync(m3u);
        
                // Get the content type from the HTTP response headers
                string contentType = client.ResponseHeaders.GetValues("Content-Type").FirstOrDefault();
            }
        
            // Process m3u content
            string[] lines = m3uContent.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            List<string> updatedLines = new List<string>();
            foreach (string line in lines)
            {
                if ((line.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || line.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) && line.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase))
                {
                    string encodedLine = HttpUtility.UrlEncode(line);
                    string newLine = $"{baseURL}{encodedLine}{(string.IsNullOrEmpty(queryString) ? string.Empty : $"?{queryString}")}";
                    updatedLines.Add(newLine);
                }
                else
                {
                    updatedLines.Add(line);
                }
            }
        
            // Send modified m3u content as response
            context.Response.ContentType = contentType ?? "application/vnd.apple.mpegurl";
            using (StreamWriter writer = new StreamWriter(context.Response.OutputStream))
            {
                foreach (string updatedLine in updatedLines)
                {
                    await writer.WriteLineAsync(updatedLine);
                }
            }
            await context.Response.OutputStream.FlushAsync();
        }

        private static async Task GetStream(IHttpContext context)
        {
            // Parse the full URL and extract relevant components
            Uri uri = new Uri(context.Request.Path);
            string m3u8 = HttpUtility.UrlDecode(uri.AbsolutePath.Substring("/getStream/".Length));
            string referer = HttpUtility.UrlDecode(HttpUtility.ParseQueryString(uri.Query).Get("referer") ?? string.Empty);
            string agent = HttpUtility.UrlDecode(HttpUtility.ParseQueryString(uri.Query).Get("agent") ?? string.Empty);

            string command = $"-q --no-warnings --downloader ffmpeg \"{m3u8}\" -o -";
            if (!string.IsNullOrEmpty(referer))
            {
                command += $" --referer \"{referer}\"";
            }
            if (string.IsNullOrEmpty(agent))
            {
                agent = Environment.GetEnvironmentVariable("AGENT");
            }
            if (!string.IsNullOrEmpty(agent))
            {
                command += $" --user-agent \"{agent}\"";
            }
            
            $"Got request to play stream {m3u8} with referer {referer} and agent {agent}. Starting now with command {command}".Log(nameof(GetStream), LogLevel.Info);
                                                                                                                                   
            Process process = Process.Start(new ProcessStartInfo
            {
                FileName = "yt-dlp",
                Arguments = command,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            });

            try
            {
                await process.StandardOutput.BaseStream.CopyToAsync(context.Response.OutputStream);
                //await process.StandardOutput.BaseStream.CopyToAsync(Console.OpenStandardOutput());

                var errors = await process.StandardError.ReadToEndAsync();
                if (!string.IsNullOrEmpty(errors))
                {
                    $"There were errors playing {m3u8}: {errors}".Log(nameof(GetStream), LogLevel.Error);
                }

                // Handle graceful shutdown (natural end-of-stream)
                // Next, wait for the stream playing process to end (with a timeout, in case it hangs)
                if (process.WaitForExit((int)TimeSpan.FromSeconds(5).TotalMilliseconds))
                {
                    $"Stream {m3u8} finished. Stream process exited gracefully is {process.HasExited}.".Log(nameof(GetStream), LogLevel.Info);
                }
                else
                {
                    // The streaming process failed to exit gracefully, so kill it.
                    process.Kill();
                    await process.WaitForExitAsync();

                    $"Stream {m3u8} finished. Stream process exited successfully (ungracefully) is {process.HasExited}.".Log(nameof(GetStream), LogLevel.Info);
                }
            }
            catch
            {
                // Handle forceful shutdown (client disconnection)

                process.Kill();
                await process.WaitForExitAsync();

                $"Client disconnected. Killing stream {m3u8}. Stream process exited successfully is {process.HasExited}.".Log(nameof(GetStream), LogLevel.Info);
            }
        }
    }
}
