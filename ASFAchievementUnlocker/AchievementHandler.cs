using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ArchiSteamFarm.Core;
using ArchiSteamFarm.Steam;
using ArchiSteamFarm.Localization;
using SteamKit2;
using SteamKit2.Internal;

namespace ASFAchievementUnlocker
{
	public sealed class AchievementHandler : ClientMsgHandler {
		public override void HandleMsg(IPacketMsg packetMsg) {
			if (packetMsg == null) {
				ASF.ArchiLogger.LogNullError(nameof(packetMsg));

				return;
			}

			switch (packetMsg.MsgType) {
				case EMsg.ClientGetUserStatsResponse:
					ClientMsgProtobuf<CMsgClientGetUserStatsResponse> getAchievementsResponse = new(packetMsg);
					Client.PostCallback(new GetAchievementsCallback(packetMsg.TargetJobID, getAchievementsResponse.Body));
					break;
				case EMsg.ClientStoreUserStatsResponse:
					ClientMsgProtobuf<CMsgClientStoreUserStatsResponse> setAchievementsResponse = new(packetMsg);
					Client.PostCallback(new SetAchievementsCallback(packetMsg.TargetJobID, setAchievementsResponse.Body));
					break;
			}

		}

		internal abstract class AchievementsCallBack<T> : CallbackMsg {
			internal readonly T Response;
			internal readonly bool Success;

			internal AchievementsCallBack(JobID jobID, T msg, Func<T, EResult> eresultGetter, string error) {
				if (jobID == null) {
					throw new ArgumentNullException(nameof(jobID));
				}

				if (msg == null) {
					throw new ArgumentNullException(nameof(msg));
				}

				JobID = jobID;
				Success = eresultGetter(msg) == EResult.OK;
				Response = msg;

				if (!Success) {
					ASF.ArchiLogger.LogGenericError(string.Format(Strings.ErrorFailingRequest, error));
				}
			}
		}

		internal sealed class GetAchievementsCallback : AchievementsCallBack<CMsgClientGetUserStatsResponse> {
			internal GetAchievementsCallback(JobID jobID, CMsgClientGetUserStatsResponse msg)
				: base(jobID, msg, msg => (EResult) msg.eresult, "GetAchievements") { }
		}

		internal sealed class SetAchievementsCallback : AchievementsCallBack<CMsgClientStoreUserStatsResponse> {
			internal SetAchievementsCallback(JobID jobID, CMsgClientStoreUserStatsResponse msg)
				: base(jobID, msg, msg => (EResult) msg.eresult, "SetAchievements") { }
		}

		//Utilities

		private List<StatData>? ParseResponse(CMsgClientGetUserStatsResponse Response) {
			List<StatData> result = new List<StatData>();
			KeyValue KeyValues = new KeyValue();
			if (Response.schema != null) {
				using (MemoryStream ms = new MemoryStream(Response.schema)) {
					if (!KeyValues.TryReadAsBinary(ms)) {
						ASF.ArchiLogger.LogGenericError(string.Format(Strings.ErrorIsInvalid, nameof(Response.schema)));
						return null;
					};
				}

				//first we enumerate all real achievements
				foreach (KeyValue stat in KeyValues.Children.Find(Child => Child.Name == "stats")?.Children ?? new List<KeyValue>()) {
					if (stat.Children.Find(Child => Child.Name == "type")?.Value == "4") {
						foreach (KeyValue Achievement in stat.Children.Find(Child => Child.Name == "bits")?.Children ?? new List<KeyValue>()) {
							if (int.TryParse(Achievement.Name, out int bitNum)) {
								if (uint.TryParse(stat.Name, out uint statNum)) {
									uint? stat_value = Response?.stats?.Find(statElement => statElement.stat_id == statNum)?.stat_value;
									bool isSet = stat_value != null && (stat_value & ((uint) 1 << bitNum)) != 0;

									bool restricted = Achievement.Children.Find(Child => Child.Name == "permission") != null;

									string? dependancyName = (Achievement.Children.Find(Child => Child.Name == "progress") == null) ? "" : Achievement.Children.Find(Child => Child.Name == "progress")?.Children?.Find(Child => Child.Name == "value")?.Children?.Find(Child => Child.Name == "operand1")?.Value;

									uint.TryParse((Achievement.Children.Find(Child => Child.Name == "progress") == null) ? "0" : Achievement.Children.Find(Child => Child.Name == "progress")!.Children.Find(Child => Child.Name == "max_val")?.Value, out uint dependancyValue);
									string lang = CultureInfo.CurrentUICulture.EnglishName.ToLower();
									if (lang.IndexOf('(') > 0) {
										lang = lang.Substring(0, lang.IndexOf('(') - 1);
									}
									if (Achievement.Children.Find(Child => Child.Name == "display")?.Children?.Find(Child => Child.Name == "name")?.Children?.Find(Child => Child.Name == lang) == null) {
										lang = "english";//fallback to english
									}

									string? name = Achievement.Children.Find(Child => Child.Name == "display")?.Children?.Find(Child => Child.Name == "name")?.Children?.Find(Child => Child.Name == lang)?.Value;
									result.Add(new StatData() {
										StatNum = statNum,
										BitNum = bitNum,
										IsSet = isSet,
										Restricted = restricted,
										DependancyValue = dependancyValue,
										DependancyName = dependancyName,
										Dependancy = 0,
										Name = name,
										StatValue = stat_value ?? 0
									});

								}
							}
						}
					}
				}
				//Now we update all dependancies
				foreach (KeyValue stat in KeyValues.Children.Find(Child => Child.Name == "stats")?.Children ?? new List<KeyValue>()) {
					if (stat.Children.Find(Child => Child.Name == "type")?.Value == "1") {
						if (uint.TryParse(stat.Name, out uint statNum)) {
							bool restricted = stat.Children.Find(Child => Child.Name == "permission") != null;
							string? name = stat.Children.Find(Child => Child.Name == "name")?.Value;
							if (name != null) {
								StatData? ParentStat = result.Find(item => item.DependancyName == name);
								if (ParentStat != null) {
									ParentStat.Dependancy = statNum;
									if (restricted && !ParentStat.Restricted) {
										ParentStat.Restricted = true;
									}
								}
							}
						}
					}
				}
			}
			return result;
		}

		private IEnumerable<CMsgClientStoreUserStats2.Stats> GetStatsToSet(List<CMsgClientStoreUserStats2.Stats> statsToSet, StatData statToSet, bool set = true) {
			if (statToSet == null) {
				yield break; //it should never happen
			}

			CMsgClientStoreUserStats2.Stats? currentstat = statsToSet.Find(stat => stat.stat_id == statToSet.StatNum);
			if (currentstat == null) {
				currentstat = new CMsgClientStoreUserStats2.Stats() {
					stat_id = statToSet.StatNum,
					stat_value = statToSet.StatValue
				};
				yield return currentstat;
			}

			uint statMask = ((uint) 1 << statToSet.BitNum);
			if (set) {
				currentstat.stat_value = currentstat.stat_value | statMask;
			} else {
				currentstat.stat_value = currentstat.stat_value & ~statMask;
			}
			if (!string.IsNullOrEmpty(statToSet.DependancyName)) {
				CMsgClientStoreUserStats2.Stats? dependancystat = statsToSet.Find(stat => stat.stat_id == statToSet.Dependancy);
				if (dependancystat == null) {
					dependancystat = new CMsgClientStoreUserStats2.Stats() {
						stat_id = statToSet.Dependancy,
						stat_value = set ? statToSet.DependancyValue : 0
					};
					yield return dependancystat;
				}
			}

		}

		//Endpoints

		internal async Task<HashSet<uint>> GetAchievements(Bot bot, ulong gameID) {

			HashSet<uint> achievements = new HashSet<uint>();

			if (!Client.IsConnected) 
			{
				return achievements;
				//return Strings.BotNotConnected;
			}

			GetAchievementsCallback? response = await GetAchievementsResponse(bot, gameID);

			if (response == null || response.Response == null || !response.Success) 
			{
				return achievements;
				//return "Can't retrieve achievements for " + gameID.ToString();
			}
			
			List<StatData>? Stats = ParseResponse(response.Response);
			if (Stats == null) {
				bot.ArchiLogger.LogNullError(nameof(Stats));
			} else if (Stats.Count == 0) {
				bot.ArchiLogger.LogNullError(nameof(Stats));
			} else {

				foreach (StatData stat in Stats) 
				{
					achievements.Add(Convert.ToUInt32(Stats.IndexOf(stat) + 1));
				}
				return achievements;
			}
			return achievements;
		}

		internal async Task<string> SetAchievements(Bot bot, uint appId, HashSet<uint> achievements, bool set = true) 
		{
			if (!Client.IsConnected) 
			{
				return Strings.BotNotConnected;
			}

			string responses = "";
			int protectedCount = 0;
			int alreadyUnlockedCount = 0;
			int sucessCount = 0;

			GetAchievementsCallback? response = await GetAchievementsResponse(bot, appId);
			if (response == null) 
			{
				bot.ArchiLogger.LogNullError(nameof(response));
				return $"<{bot.BotName}> AppID:{appId} -- Can't retrieve achievements for " + appId.ToString(); ;
			}

			if (!response.Success) 
			{
				return $"<{bot.BotName}> AppID:{appId} -- Can't retrieve achievements for " + appId.ToString(); ;
			}

			if (response.Response == null)
			{
				bot.ArchiLogger.LogNullError(nameof(response.Response));
				return responses = $"<{bot.BotName}> AppID:{appId} -- {Strings.WarningFailed}";
			}

			List<StatData>? Stats = ParseResponse(response.Response);
			if (Stats == null) 
			{
				return responses = $"<{bot.BotName}> AppID:{appId} -- {Strings.WarningFailed}";
			}

			List<CMsgClientStoreUserStats2.Stats> statsToSet = new List<CMsgClientStoreUserStats2.Stats>();

			if (achievements.Count == 0) { //if no parameters provided - set/reset all. Don't kill me Archi.

				foreach (StatData stat in Stats.Where(s => !s.Restricted))
				{
					statsToSet.AddRange(GetStatsToSet(statsToSet, stat, set));
				}

			} else 
			{
				foreach (uint achievement in achievements) 
				{
					if (Stats.Count < achievement) 
					{
						continue;
					}

					if (Stats[(int) achievement - 1].IsSet == set) 
					{
						alreadyUnlockedCount++;
						continue;
					}
					if (Stats[(int) achievement - 1].Restricted) {
						protectedCount++;
						continue;
					}

					sucessCount++;
					statsToSet.AddRange(GetStatsToSet(statsToSet, Stats[(int) achievement - 1], set));
				}
			}

			if (statsToSet.Count == 0) 
			{
				return responses = $"<{bot.BotName}> AppID:{appId} -- {Strings.WarningFailed} -- No achievements found to unlock!";
			};

			ClientMsgProtobuf<CMsgClientStoreUserStats2> request = new ClientMsgProtobuf<CMsgClientStoreUserStats2>(EMsg.ClientStoreUserStats2) {
				SourceJobID = Client.GetNextJobID(),
				Body = {
					game_id = (uint) appId,
					settor_steam_id = (ulong)bot.SteamID,
					settee_steam_id = (ulong)bot.SteamID,
					explicit_reset = false,
					crc_stats = response.Response.crc_stats
				}
			};
			request.Body.stats.AddRange(statsToSet);
			Client.Send(request);

			SetAchievementsCallback setResponse = await new AsyncJob<SetAchievementsCallback>(Client, request.SourceJobID).ToLongRunningTask().ConfigureAwait(false);
			return responses = $"<{bot.BotName}> AppID:{appId} -- Already Unlocked: {alreadyUnlockedCount} -- Set Sucess: {sucessCount} -- Protecteds: {protectedCount}";
		}

		private async Task<GetAchievementsCallback?> GetAchievementsResponse(Bot bot, ulong gameID) {
			if (!Client.IsConnected) {
				return null;
			}

			ClientMsgProtobuf<CMsgClientGetUserStats> request = new ClientMsgProtobuf<CMsgClientGetUserStats>(EMsg.ClientGetUserStats) {
				SourceJobID = Client.GetNextJobID(),
				Body = {
					game_id =  gameID,
					steam_id_for_user = bot.SteamID,
				}
			};

			Client.Send(request);

			return await new AsyncJob<GetAchievementsCallback>(Client, request.SourceJobID).ToLongRunningTask().ConfigureAwait(false);
		}
	}
}
