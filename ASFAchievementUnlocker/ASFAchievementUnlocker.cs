using ArchiSteamFarm.Core;
using ArchiSteamFarm.Localization;
using ArchiSteamFarm.Plugins.Interfaces;
using ArchiSteamFarm.Steam;
using ArchiSteamFarm.Steam.Interaction;
using ArchiSteamFarm.Steam.Storage;
using SteamKit2;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Composition;
using System.Linq;
using System.Threading.Tasks;

namespace ASFAchievementUnlocker
{
    [Export(typeof(IPlugin))]
    public class ASFAchievementUnlocker : IBotCommand, IBotSteamClient
    {
        public string Name => "ASF Achievement Unlocker By Cappi_1998";

        public Version Version => typeof(ASFAchievementUnlocker).Assembly.GetName().Version ?? throw new InvalidOperationException(nameof(Version));

        private static ConcurrentDictionary<Bot, AchievementHandler> AchievementHandlers = new ConcurrentDictionary<Bot, AchievementHandler>();

        public void OnLoaded() { }

        public async Task<string> OnBotCommand(Bot bot, ulong steamID, string message, string[] args)
        {
            if (!bot.HasAccess(steamID, BotConfig.EAccess.Master))
            {
                return null;
            }

            switch (args[0].ToUpperInvariant())
            {
                case "UNLOCKER" when args.Length >= 3:
                    {
                        string botname = args[1];
                        string AppIDs = Utilities.GetArgsAsText(args, 2, ",");
                        return await ResponseUnlockerAchievements(botname, AppIDs).ConfigureAwait(false);
                    }
                default:
                    {
                        return $"Error, wrong format!{Environment.NewLine}Use: unlocker {{BotName}} {{AppID,AppID}}{Environment.NewLine}Use: unlocker asf {{AppID,AppID}} to run on all bots";
                    }
            }
        }

        private static async Task<string> ResponseUnlockerAchievements(string botNames, string AppIDs)
        {
            HashSet<Bot> bots = Bot.GetBots(botNames);
            if ((bots == null) || (bots.Count == 0))
            {
                return Commands.FormatStaticResponse(string.Format(Strings.BotNotFound, botNames));
            }

            IList<string> results = await Utilities.InParallel(bots.Select(bot => UnlockerAchievements(bot, AppIDs))).ConfigureAwait(false);

            List<string> responses = new List<string>(results.Where(result => !string.IsNullOrEmpty(result)));

            return responses.Count > 0 ? string.Join(Environment.NewLine, responses) : null;
        }

        private static async Task<string> UnlockerAchievements(Bot bot, string AppIDs)
        {
            if (!AchievementHandlers.TryGetValue(bot, out AchievementHandler? AchievementHandler))
            {
                return bot.Commands.FormatBotResponse(string.Format(Strings.ErrorIsEmpty, nameof(AchievementHandlers)));
            }

            HashSet<uint> gamesToGetAchievements = new HashSet<uint>();
            string[] gameIDs = AppIDs.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string game in gameIDs)
            {
                if (!uint.TryParse(game, out uint gameID) || (gameID == 0))
                {
                    return bot.Commands.FormatBotResponse(string.Format(Strings.ErrorParsingObject, nameof(gameID)));
                }
                gamesToGetAchievements.Add(gameID);
            }

            var response = "";
            foreach(var AppID in gamesToGetAchievements)
            {
                if (bot.IsConnectedAndLoggedOn)
                {
                    HashSet<uint> achievements = await AchievementHandler.GetAchievements(bot, Convert.ToUInt64(AppID)).ConfigureAwait(false);
                    response += await Task.Run<string>(() => AchievementHandler.SetAchievements(bot, AppID, achievements, true)).ConfigureAwait(false);
                }
                else
                {
                    return Strings.BotNotConnected;
                }
            }
            return Strings.ErrorAborted;
        }

        public void OnBotSteamCallbacksInit(Bot bot, CallbackManager callbackManager) {}

        public IReadOnlyCollection<ClientMsgHandler> OnBotSteamHandlersInit(Bot bot)
        {
            AchievementHandler CurrentBotAchievementHandler = new AchievementHandler();
            AchievementHandlers.TryAdd(bot, CurrentBotAchievementHandler);
            return new HashSet<ClientMsgHandler> { CurrentBotAchievementHandler };
        }
    }
}
