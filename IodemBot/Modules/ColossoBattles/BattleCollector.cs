﻿using Discord;
using Discord.Rest;
using Discord.WebSocket;
using IodemBot.Core.Leveling;
using IodemBot.Core.UserManagement;
using IodemBot.Extensions;
using IodemBot.Modules.GoldenSunMechanics;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Timers;
using static IodemBot.Modules.ColossoBattles.ColossoPvE;

namespace IodemBot.Modules.ColossoBattles
{
    internal class BattleCollector : IDisposable
    {
        internal string Name;
        internal ITextChannel TextChannel = LobbyChannel;
        private readonly int stageLength = 12;
        private Timer autoTurn;
        private bool isProcessing = false;
        private int LureCaps = 0;
        private List<SocketReaction> reactions = new List<SocketReaction>();
        private Timer resetIfNotActive;
        private int winsInARow = 0;

        public BattleCollector()
        {
            Global.Client.ReactionAdded += ReactionAdded;
        }

        public bool IsEndless { get; internal set; } = false;
        internal ITextChannel BattleChannel { get; set; }

        internal double Boost
        {
            get
            {
                if (winsInARow <= 3 * stageLength)
                {
                    return 1.0 + (double)winsInARow % (stageLength + 1) / 30;
                }
                else

                {
                    return 1.0 + (double)(winsInARow - 3 * stageLength) / 30;
                }
            }
        }

        internal BattleDifficulty Diff { get; set; } = BattleDifficulty.Easy;
        internal IUserMessage EnemyMsg { get; set; }
        internal Dictionary<IUserMessage, ColossoFighter> Messages { get; set; } = new Dictionary<IUserMessage, ColossoFighter>();
        internal uint PlayersToStart { get; set; } = 4;
        internal IUserMessage StatusMsg { get; set; }
        private ColossoBattle Battle { get; set; }

        public void Dispose()
        {
            Global.Client.ReactionAdded -= ReactionAdded;
            Messages.Clear();
            autoTurn.Dispose();
            resetIfNotActive.Dispose();
        }

        internal async Task GameOver()
        {
            var winners = Battle.GetTeam(Battle.GetWinner());
            if (Battle.SizeTeamB == 0)
            {
                Console.WriteLine("Game Over with no enemies existing.");
            }
            if (winners.First() is PlayerFighter)
            {
                winsInARow++;
                var wasMimic = Battle.TeamB.Any(e => e.name.Contains("Mimic"));
                winners.ConvertAll(s => (PlayerFighter)s).ForEach(async p => await ServerGames.UserWonBattle(p.avatar, winsInARow, LureCaps, p.battleStats, Diff, TextChannel, winners, wasMimic));
                if (!IsEndless)
                {
                    _ = WriteGameOver();
                }
                else
                {
                    Battle.TeamA.ForEach(p =>
                    {
                        p.PPrecovery += (winsInARow <= 8 * 4 && winsInARow % 4 == 0) ? 1 : 0;
                        p.RemoveNearlyAllConditions();
                        p.Buffs = new List<Buff>();
                        p.Heal((uint)(p.stats.HP * 5 / 100));
                    });

                    var text = $"{winners.First().name}'s Party wins Battle {winsInARow}! Battle will reset shortly";
                    await Task.Delay(2000);
                    await StatusMsg.ModifyAsync(m => { m.Content = text; m.Embed = null; });

                    await Task.Delay(2000);

                    Diff = (BattleDifficulty)Math.Min(4, 1 + winsInARow / stageLength);
                    SetRandomEnemies(ColossoBattle.Team.B);
                    Battle.turn = 0;
                    _ = StartBattle();
                }
            }
            else
            {
                if (IsEndless)
                {
                    Diff = BattleDifficulty.Easy;
                }

                var losers = winners.First().battle.GetTeam(winners.First().enemies);
                losers.ConvertAll(s => (PlayerFighter)s).ForEach(async p => await ServerGames.UserLostBattle(p.avatar, Diff, TextChannel));
                _ = WriteGameOver();
            }
        }

        internal async Task ProcessTurn(bool forced)
        {
            isProcessing = true;
            bool turnProcessed = forced ? Battle.ForceTurn() : Battle.Turn();

            if (turnProcessed)
            {
                autoTurn.Stop();
                await WriteBattle();
                if (Battle.isActive)
                {
                    autoTurn.Start();
                }
                else
                {
                    await GameOver();
                }
            };
            isProcessing = false;
        }

        internal async Task ReactionAdded(Cacheable<IUserMessage, ulong> cache, ISocketMessageChannel channel, SocketReaction reaction)
        {
            try
            {
                if (reaction.User.Value.IsBot)
                {
                    return;
                }
                if (channel.Id != BattleChannel.Id)
                {
                    return;
                }
                if (reaction.Emote.Name == "Fight")
                {
                    _ = AddPlayer(reaction);
                    return;
                }
                else if (reaction.Emote.Name == "Battle")
                {
                    _ = StartBattle();
                    return;
                }

                IUserMessage c = null;
                if (StatusMsg.Id == reaction.MessageId)
                {
                    c = StatusMsg;
                }
                if (EnemyMsg.Id == reaction.MessageId)
                {
                    c = EnemyMsg;
                }
                if (Messages.Keys.Any(k => k.Id == reaction.MessageId))
                {
                    c = Messages.Keys.Where(k => k.Id == reaction.MessageId).First();
                }

                if (c == null)
                {
                    c = (RestUserMessage)await channel.GetMessageAsync(reaction.MessageId);
                    _ = c.RemoveReactionAsync(reaction.Emote, reaction.User.Value);
                    Console.WriteLine("No matching Message for User found.");
                    return;
                }

                if (!Battle.isActive)
                {
                    _ = c.RemoveReactionAsync(reaction.Emote, reaction.User.Value);
                    Console.WriteLine("Battle not active.");
                    return;
                }

                if (Battle.turnActive)
                {
                    _ = c.RemoveReactionAsync(reaction.Emote, reaction.User.Value);
                    Console.WriteLine("Not so fast");
                    return;
                }

                if (reaction.Emote.Name == "🔄")
                {
                    await c.RemoveReactionAsync(reaction.Emote, reaction.User.Value);
                    autoTurn.Stop();
                    Task.WaitAll(Messages.Select(m => m.Key.RemoveAllReactionsAsync()).Append(EnemyMsg.RemoveAllReactionsAsync()).ToArray());

                    _ = WriteBattleInit();
                    autoTurn.Start();
                    return;
                }

                if (reaction.Emote.Name == "⏸")
                {
                    autoTurn.Stop();
                    return;
                }

                if (reaction.Emote.Name == "▶")
                {
                    autoTurn.Start();
                    return;
                }

                if (reaction.Emote.Name == "⏩")
                {
                    _ = ProcessTurn(true);
                    return;
                }

                var curPlayer = Messages.Values.Where(p => p.name == ((SocketGuildUser)reaction.User.Value).DisplayName()).FirstOrDefault();
                var correctID = Messages.Keys.Where(key => Messages[key].name == curPlayer.name).First().Id;

                if (!numberEmotes.Contains(reaction.Emote.Name))
                {
                    if (reaction.MessageId != EnemyMsg.Id && reaction.MessageId != correctID)
                    {
                        _ = c.RemoveReactionAsync(reaction.Emote, reaction.User.Value);
                        Console.WriteLine("Didn't click on own message.");
                        return;
                    }
                }

                if (isProcessing)
                {
                    _ = c.RemoveReactionAsync(reaction.Emote, reaction.User.Value);
                    Console.WriteLine("Still processing.");
                    return;
                }
                if (!curPlayer.Select(reaction.Emote.Name))
                {
                    _ = c.RemoveReactionAsync(reaction.Emote, reaction.User.Value);
                    Console.WriteLine("Couldn't select that move.");
                    return;
                }
                reactions.Add(reaction);

                _ = ProcessTurn(forced: false);
            }
            catch (Exception e)
            {
                Console.WriteLine("Colosso Turn Processing Error: " + e.Message);
                File.WriteAllText($"Logs/Crashes/Error_{DateTime.Now.Date}.log", e.Message);
            }
        }

        internal async Task Reset()
        {
            Battle = new ColossoBattle();

            if (autoTurn != null)
            {
                autoTurn.Dispose();
            }
            if (resetIfNotActive != null)
            {
                resetIfNotActive.Dispose();
            }

            foreach (var k in Messages.Keys)
            {
                await k.DeleteAsync();
            }

            Messages.Clear();

            if (EnemyMsg != null)
            {
                _ = EnemyMsg.ModifyAsync(c => { c.Content = $"Welcome to {Name} Battle!\n\nReact with <:Fight:536919792813211648> to join the {Name} Battle and press <:Battle:536954571256365096> when you are ready to battle!"; c.Embed = null; });
                await EnemyMsg.RemoveAllReactionsAsync();
                _ = EnemyMsg.AddReactionsAsync(new IEmote[]
                {
                        Emote.Parse("<:Fight:536919792813211648>"),
                        Emote.Parse("<:Battle:536954571256365096>")
                });
            }
            if (StatusMsg != null)
            {
                _ = StatusMsg.DeleteAsync();
                StatusMsg = null;
            }
            winsInARow = 0;
            LureCaps = 0;
            SetRandomEnemies(ColossoBattle.Team.B);

            autoTurn = new Timer()
            {
                Interval = 25000,
                AutoReset = false,
                Enabled = false
            };
            autoTurn.Elapsed += OnTimerTicked;
            resetIfNotActive = new Timer()
            {
                Interval = 120000,
                AutoReset = false,
                Enabled = false
            };
            resetIfNotActive.Elapsed += BattleWasNotStartetInTime;

            Console.WriteLine("Battle was reset.");
        }

        internal void SetEnemy(string enemy)
        {
            Battle.TeamB = new List<ColossoFighter>();
            EnemiesDatabase.GetEnemies(Diff, enemy).ForEach(f => Battle.AddPlayer(f, ColossoBattle.Team.B));
            Console.WriteLine($"Up against {Battle.TeamB.First().name}");
        }

        internal void SetRandomEnemies(ColossoBattle.Team team)
        {
            Battle.GetTeam(team).Clear();
            Console.WriteLine(Boost);
            EnemiesDatabase.GetRandomEnemies(Diff, Boost).ForEach(f =>
            {
                Battle.AddPlayer(f, ColossoBattle.Team.B);
            }
            );

            for (int i = 0; i < LureCaps; i++)
            {
                if (Battle.GetTeam(team).Count < 9)
                {
                    Battle.AddPlayer(EnemiesDatabase.GetRandomEnemies(Diff, Boost).Random(), team);
                }
            }
            Console.WriteLine($"Up against {Battle.TeamB.First().name}");
        }

        internal async Task WriteBattle()
        {
            await Task.WhenAll(new[]
            {
                WriteStatus(),
                WriteEnemies(),
                WritePlayers()
            });
        }

        internal async Task WriteBattleInit()
        {
            await Task.WhenAll(new[]
{
                WriteStatusInit(),
                WriteEnemiesInit(),
                WritePlayersInit()
            });
        }

        private async Task AddPlayer(SocketReaction reaction)
        {
            if (Battle.isActive)
            {
                return;
            }

            if (Messages.Values.Where(v => v is PlayerFighter).Where(s => ((PlayerFighter)s).avatar.ID == reaction.UserId).Any())
            {
                return;
            }

            SocketGuildUser player = (SocketGuildUser)reaction.User.Value;
            var playerAvatar = UserAccounts.GetAccount(player);

            var p = new PlayerFighter(player);
            Battle.AddPlayer(p, ColossoBattle.Team.A);

            if (playerAvatar.Inv.GetGear(AdeptClassSeriesManager.GetClassSeries(playerAvatar).Archtype).Any(i => i.Name == "Lure Cap"))
            {
                LureCaps++;
                SetRandomEnemies(ColossoBattle.Team.B);
            }

            if (Name == "Bronze")
            {
                if (playerAvatar.LevelNumber < 10 && Messages.Count == 0)
                {
                    Diff = BattleDifficulty.Tutorial;
                    SetRandomEnemies(ColossoBattle.Team.B);
                }
                else
                {
                    if (Diff != BattleDifficulty.Easy)
                    {
                        Diff = BattleDifficulty.Easy;
                        SetRandomEnemies(ColossoBattle.Team.B);
                    }
                }
            }

            var playerMsg = await BattleChannel.SendMessageAsync($"{player.DisplayName()} wants to battle!");
            Messages.Add(playerMsg, p);
            resetIfNotActive.Start();

            if (Messages.Count == PlayersToStart)
            {
                await StartBattle();
            }
        }

        private async void BattleWasNotStartetInTime(object sender, ElapsedEventArgs e)
        {
            await Reset();
        }

        private async void OnTimerTicked(object sender, ElapsedEventArgs e)
        {
            _ = ProcessTurn(forced: true);
            await Task.CompletedTask;
        }

        private async Task StartBattle()
        {
            if (Battle.isActive)
            {
                return;
            }

            if (Battle.SizeTeamA == 0)
            {
                return;
            }

            resetIfNotActive.Stop();
            Battle.Start();
            await WriteBattleInit();
            autoTurn.Start();
            // await Task.CompletedTask;
        }

        private async Task WriteEnemies()
        {
            var e = new EmbedBuilder();
            var tasks = new List<Task>();
            if (Battle.SizeTeamB > 0)
            {
                e.WithThumbnailUrl(Battle.GetTeam(ColossoBattle.Team.B).FirstOrDefault().imgUrl);
            }
            var msg = EnemyMsg;
            var i = 1;
            foreach (ColossoFighter fighter in Battle.GetTeam(ColossoBattle.Team.B))
            {
                //e.AddField(numberEmotes[i], $"{fighter.name} {fighter.stats.HP}/{fighter.stats.maxHP}", true);
                e.AddField($"{numberEmotes[i]}{fighter.ConditionsToString()}", $"{fighter.name}", true);
                i++;
            }
            if (IsEndless)
            {
                EmbedFooterBuilder footerBuilder = new EmbedFooterBuilder();
                footerBuilder.WithText($"Battle {winsInARow + 1} - {Diff}");
                e.WithFooter(footerBuilder);
            }
            if (!msg.Embeds.FirstOrDefault().ToEmbedBuilder().AllFieldsEqual(e))
            {
                tasks.Add(msg.ModifyAsync(m => m.Embed = e.Build()));
            }

            var validReactions = reactions.Where(r => r.MessageId == EnemyMsg.Id).ToList();
            foreach (var r in validReactions)
            {
                tasks.Add(EnemyMsg.RemoveReactionAsync(r.Emote, r.User.Value));
                reactions.Remove(r);
            }
            await Task.WhenAll(tasks);
        }

        private async Task WriteEnemiesInit()
        {
            var tasks = new List<Task>();
            if (Battle.SizeTeamB == 0)
            {
                Console.WriteLine("Here!!");
            }
            var e = new EmbedBuilder();
            if (Battle.SizeTeamB > 0)
            {
                e.WithThumbnailUrl(Battle.GetTeam(ColossoBattle.Team.B).FirstOrDefault().imgUrl);
            }
            var msg = EnemyMsg;
            var i = 1;
            foreach (ColossoFighter fighter in Battle.GetTeam(ColossoBattle.Team.B))
            {
                e.AddField($"{numberEmotes[i]} {fighter.ConditionsToString()}", $"{fighter.name}", true);
                i++;
            }
            if (IsEndless)
            {
                EmbedFooterBuilder footerBuilder = new EmbedFooterBuilder();
                footerBuilder.WithText($"Battle {winsInARow + 1} - {Diff}");
                e.WithFooter(footerBuilder);
            }
            tasks.Add(msg.ModifyAsync(m => { m.Content = ""; m.Embed = e.Build(); }));

            var oldReactionCount = EnemyMsg.Reactions.Where(k => numberEmotes.Contains(k.Key.Name)).Count();
            if (winsInARow == 0 && Battle.turn == 0)
            {
                await msg.RemoveAllReactionsAsync();
            }

            if (Battle.SizeTeamB == oldReactionCount)
            {
            }
            else if (Battle.SizeTeamB <= 1)
            {
                if (oldReactionCount > 0)
                {
                    tasks.Add(msg.RemoveAllReactionsAsync());
                }
            }
            else if (Battle.SizeTeamB > oldReactionCount)
            {
                tasks.Add(msg.AddReactionsAsync(
                    numberEmotes
                    .Skip(Math.Max(1, oldReactionCount))
                    .Take(Battle.SizeTeamB - Math.Max(0, oldReactionCount - 1))
                    .Select(s => new Emoji(s))
                    .ToArray()));
            }
            else if (oldReactionCount - Battle.SizeTeamB <= Battle.SizeTeamB + 1)
            {
                var reactionsToRemove = msg.Reactions.Where(k => numberEmotes.Skip(Battle.SizeTeamB + 1).Contains(k.Key.Name)).ToArray();
                tasks.Add(msg.RemoveReactionsAsync(msg.Author, reactionsToRemove.Select(d => d.Key).ToArray()));
            }
            else
            {
                if (oldReactionCount > 0)
                {
                    await msg.RemoveAllReactionsAsync();
                }

                tasks.Add(msg.AddReactionsAsync(
                   numberEmotes
                   .Skip(1)
                   .Take(Battle.SizeTeamB)
                   .Select(s => new Emoji(s))
                   .ToArray()));
            }

            if (Battle.SizeTeamB == 0)
            {
                Console.WriteLine("Here!!");
            }
            await Task.WhenAll(tasks);
        }

        private async Task WriteGameOver()
        {
            await Task.Delay(2000);
            var winners = Battle.GetTeam(Battle.GetWinner());
            var text = $"{winners.FirstOrDefault().name}'s Party wins! Battle will reset shortly";
            await StatusMsg.ModifyAsync(m => { m.Content = text; m.Embed = null; });
            await Task.Delay(2000);
            await Reset();
        }

        private async Task WritePlayers()
        {
            int i = 1;
            var tasks = new List<Task>();
            foreach (KeyValuePair<IUserMessage, ColossoFighter> k in Messages)
            {
                var msg = k.Key;
                var embed = new EmbedBuilder();
                var fighter = k.Value;

                var validReactions = reactions.Where(r => r.MessageId == msg.Id).ToList();
                foreach (var r in validReactions)
                {
                    tasks.Add(msg.RemoveReactionAsync(r.Emote, r.User.Value));
                    reactions.Remove(r);
                }
                //e.WithAuthor($"{numberEmotes[i]} {fighter.name}");
                embed.WithThumbnailUrl(fighter.imgUrl);
                embed.WithColor(Colors.Get(fighter.moves.Where(m => m is Psynergy).Select(m => (Psynergy)m).Select(p => p.element.ToString()).ToArray()));
                //e.AddField();
                embed.AddField($"{numberEmotes[i]}{fighter.ConditionsToString()}", fighter.name);
                embed.AddField("HP", $"{fighter.stats.HP} / {fighter.stats.MaxHP}", true);
                embed.AddField("PP", $"{fighter.stats.PP} / {fighter.stats.MaxPP}", true);
                var s = new List<string>();
                foreach (var m in fighter.moves)
                {
                    if (m is Psynergy)
                    {
                        s.Add($"{m.emote} {m.name} {((Psynergy)m).PPCost}");
                    }
                    else
                    {
                        s.Add($"{m.emote} {m.name}");
                    }
                }
                embed.AddField("Psynergy", string.Join(" | ", s));

                if (msg.Embeds.Count == 0 || !msg.Embeds.FirstOrDefault().ToEmbedBuilder().AllFieldsEqual(embed))
                {
                    tasks.Add(msg.ModifyAsync(m => { m.Content = $""; m.Embed = embed.Build(); }));
                }

                if (fighter is PlayerFighter && ((PlayerFighter)fighter).AutoTurnsInARow >= 2)
                {
                    var ping = await msg.Channel.SendMessageAsync($"<@{((PlayerFighter)fighter).avatar.ID}>");
                    await ping.DeleteAsync();
                }
                i++;
            }
            await Task.WhenAll(tasks);
        }

        private async Task WritePlayersInit()
        {
            int i = 1;
            var tasks = new List<Task>();
            foreach (KeyValuePair<IUserMessage, ColossoFighter> k in Messages)
            {
                var msg = k.Key;
                var fighter = k.Value;
                List<IEmote> emotes = new List<IEmote>();
                if (Messages.Count > 1)
                {
                    emotes.Add(new Emoji(numberEmotes[i]));
                }
                foreach (var m in fighter.moves)
                {
                    IEmote e;
                    try
                    {
                        if (m.emote.StartsWith("<"))
                        {
                            e = Emote.Parse(m.emote);
                        }
                        else
                        {
                            e = new Emoji(m.emote);
                        }
                    }
                    catch
                    {
                        e = new Emoji("⛔");
                    }

                    emotes.Add(e);
                }
                emotes.RemoveAll(e => msg.Reactions.Any(r => r.Key.Name.Equals(e.Name, StringComparison.InvariantCultureIgnoreCase)));
                tasks.Add(msg.AddReactionsAsync(emotes.ToArray()));
                i++;
            }
            tasks.Add(WritePlayers());
            await Task.WhenAll(tasks);
        }

        private async Task WriteStatus()
        {
            if (Battle.log.Count > 0 && Battle.turn > 0)
            {
                if (StatusMsg == null)
                {
                    StatusMsg = await BattleChannel.SendMessageAsync(Battle.log.Aggregate("", (s, l) => s += l + "\n"));
                }
                else
                {
                    await StatusMsg.ModifyAsync(c => c.Content = Battle.log.Aggregate("", (s, l) => s += l + "\n"));
                }
            }
            else
            {
                if (StatusMsg == null)
                {
                    string msg = Messages.Values
                        .Where(p => p is PlayerFighter)
                        .Aggregate("", (s, v) => s += $"<@{((PlayerFighter)v).avatar.ID}>, ");
                    StatusMsg = await BattleChannel.SendMessageAsync($"{msg}get in position!");
                }
            }
        }

        private async Task WriteStatusInit()
        {
            await WriteStatus();
        }
    }
}