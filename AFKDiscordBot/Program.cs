using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Discord;
using Discord.WebSocket;

namespace AFKDiscordBot
{
    static class Program
    {
        private class AFKUser
        {
            public ulong Id { get; set; }
            public ulong GuildId { get; set; }
            public DateTime Timestamp { get; set; }
        }

        public static async Task Main(string[] args)
        {
            var token = Environment.GetEnvironmentVariable("DISCORD_TOKEN");
            if(string.IsNullOrWhiteSpace(token))
            {
                Console.Error.WriteLine("DISCORD_TOKEN was not found");
                Environment.Exit(-1);
            }

            var cts = new CancellationTokenSource();
            var users = new ConcurrentDictionary<ulong, AFKUser>();
            Console.CancelKeyPress += (sender, e) =>
            {
                e.Cancel = true;
                cts.Cancel();
            };

            Task UserVoiceStateUpdatedAsync(SocketUser user, SocketVoiceState oldState, SocketVoiceState newState)
            {
                if (user.IsBot || user.IsWebhook)
                {
                    return Task.CompletedTask;
                }

                if (newState.VoiceChannel == null || !(newState.IsSelfDeafened && newState.IsSelfMuted))
                {
                    users.TryRemove(user.Id, out _);
                }
                else if (newState.IsSelfDeafened && newState.IsSelfMuted)
                {

                    if (newState.VoiceChannel == newState.VoiceChannel.Guild.AFKChannel)
                    {
                        return Task.CompletedTask;
                    }

                    users.TryAdd(user.Id, new AFKUser()
                    {
                        Id = user.Id,
                        GuildId = newState.VoiceChannel.Guild.Id,
                        Timestamp = DateTime.UtcNow
                    });
                }

                return Task.CompletedTask;
            }

            using (var client = new DiscordShardedClient())
            {
                client.UserVoiceStateUpdated += UserVoiceStateUpdatedAsync;
                await client.LoginAsync(TokenType.Bot, "");
                await client.StartAsync();

                while (!cts.Token.IsCancellationRequested)
                {
                    var now = DateTime.UtcNow;
                    foreach (var user in users.Values)
                    {
                        try
                        {
                            var guid = client.GetGuild(user.GuildId);
                            if (guid == null)
                            {
                                continue;
                            }

                            if ((now - user.Timestamp).TotalSeconds >= guid.AFKTimeout)
                            {
                                var guidUser = guid.GetUser(user.Id);

                                if (guidUser == null)
                                {
                                    continue;
                                }

                                var options = RequestOptions.Default;
                                options.AuditLogReason = $"User {guidUser.Nickname} was idle, moving to AFK channel";
                                await guidUser.ModifyAsync(x => x.Channel = guid.AFKChannel, options);
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine("Failed to check/move UserId {0}: {1}", user.Id, ex.Message);
                        }
                    }

                    await Task.Delay(TimeSpan.FromMinutes(1), cts.Token);
                }
            }
        }
    }
}
