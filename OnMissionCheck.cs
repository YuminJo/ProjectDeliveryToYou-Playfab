using System;
using System.Threading.Tasks;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using PlayFab.Samples;
using System.Collections.Generic;
using PlayFab;
using PlayFab.Json;
using PlayFab.ServerModels;
using System.Linq;
using PlayFab.MultiplayerModels;

namespace DeliveryToYou.Function
{
    public static class OnMissionCheck
    {
        public class MissionData
        {
            public string MissionName;
            public int MissionCount;
            public int MissionReward;
        }

        [FunctionName("OnMissionCheck")]
        public static async Task<dynamic> OnMissionChecks(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", "post", Route = null)] HttpRequest req,
            ILogger log)
        {
            try
            {
                string body = await req.ReadAsStringAsync();
                var context = JsonConvert.DeserializeObject<FunctionExecutionContext<dynamic>>(body);
                var args = context.FunctionArgument;

                var apiSettings = new PlayFabApiSettings
                {
                    TitleId = context.TitleAuthenticationContext.Id,
                    DeveloperSecretKey = Environment.GetEnvironmentVariable("PLAYFAB_DEV_SECRET_KEY", EnvironmentVariableTarget.Process),
                };

                var serverApi = new PlayFabServerInstanceAPI(apiSettings);
                var playFabId = context.CallerEntityProfile.Lineage.MasterPlayerAccountId;

                if (args == null)
                {
                    return new BadRequestObjectResult("Invalid request data.");
                }

                const string CURRENCYGOLD = "GD";
                const string CURRENCYCASH = "CH";

                List<StatisticUpdate> statisticupdate = new();
                int ADDGOLDVALUE = 0;
                int ADDCASHVALUE = 0;
                int ADDDAILYALLMISSIONVALUE = 0;
                int ADDWEEKLYALLMISSIONVALUE = 0;

                var playerStatisticsResponse = await serverApi.GetPlayerStatisticsAsync(new GetPlayerStatisticsRequest
                {
                    PlayFabId = playFabId,
                });

                // 타이틀 데이터 가져오기
                var getTitleDatam = await serverApi.GetTitleDataAsync(new GetTitleDataRequest { });
                var getTitleDataJson = getTitleDatam.Result.Data["Mission"];
                Dictionary<string, List<Dictionary<string, int>>> missionDictionary = JsonConvert.DeserializeObject<Dictionary<string, List<Dictionary<string, int>>>>(getTitleDataJson);

                var ResultMission = new Dictionary<string, int>();

                // JObject의 각 키-값 쌍을 List<string>으로 변환
                List<string> keyValueList = new List<string>();
                foreach (var property in args.Properties())
                {
                    string keyValueString = property.Value;
                    keyValueList.Add(keyValueString);
                }

                foreach (var missionName in keyValueList)
                {
                    string missionType = missionName.Contains("DAILYMISSION") ? "DailyMission" : "WeeklyMission";

                    var playerStatistic = playerStatisticsResponse.Result.Statistics.FirstOrDefault(item => item.StatisticName == missionName);
                    if (playerStatistic == null || playerStatistic.Value <= 0)
                    {
                        continue;
                    }

                    var missionData = missionDictionary[missionType].Select(mission => new MissionData
                    {
                        MissionName = mission.Keys.FirstOrDefault(k => k != "Reward"),
                        MissionCount = mission.Values.FirstOrDefault(),
                        MissionReward = mission["Reward"]
                    }).FirstOrDefault(x => x.MissionName == playerStatistic.StatisticName);

                    if (missionData == null || missionData.MissionCount > playerStatistic.Value)
                    {
                        continue;
                    }

                    if (missionName.Contains("DAILYMISSION"))
                    {
                        ADDDAILYALLMISSIONVALUE++;
                    }
                    else
                    {
                        ADDWEEKLYALLMISSIONVALUE++;
                    }

                    if (missionName.Contains("ALLCOMPLETE"))
                    {
                        ADDCASHVALUE += missionData.MissionReward;
                        if (missionName.Contains("DAILY"))
                        {
                            ADDDAILYALLMISSIONVALUE -= 100000;
                        }
                        else
                        {
                            ADDWEEKLYALLMISSIONVALUE -= 100000;
                        }
                    }
                    else
                    {
                        ADDGOLDVALUE += missionData.MissionReward;
                        statisticupdate.Add(new StatisticUpdate
                        {
                            StatisticName = missionData.MissionName,
                            Value = -100000
                        });
                    }

                    ResultMission.Add(missionName, missionData.MissionReward);
                }

                // 플레이어 통계 최신화2
                if (ADDDAILYALLMISSIONVALUE != 0)
                {
                    statisticupdate.Add(new StatisticUpdate
                    {
                        StatisticName = "DAILYMISSION_ALLCOMPLETE",
                        Value = ADDDAILYALLMISSIONVALUE
                    });
                }

                if (ADDWEEKLYALLMISSIONVALUE != 0)
                {
                    statisticupdate.Add(new StatisticUpdate
                    {
                        StatisticName = "WEEKLYMISSION_ALLCOMPLETE",
                        Value = ADDWEEKLYALLMISSIONVALUE
                    });
                }

                var updatePlayerStatisticsRequest = new UpdatePlayerStatisticsRequest
                {
                    PlayFabId = playFabId,
                    Statistics = statisticupdate
                };

                await serverApi.UpdatePlayerStatisticsAsync(updatePlayerStatisticsRequest);

                // 가상 화폐 추가
                if (ADDGOLDVALUE > 0)
                {
                    await serverApi.AddUserVirtualCurrencyAsync(new AddUserVirtualCurrencyRequest
                    {
                        Amount = ADDGOLDVALUE,
                        PlayFabId = playFabId,
                        VirtualCurrency = CURRENCYGOLD
                    });
                }

                // 가상 화폐 추가
                if (ADDCASHVALUE > 0)
                {
                    await serverApi.AddUserVirtualCurrencyAsync(new AddUserVirtualCurrencyRequest
                    {
                        Amount = ADDCASHVALUE,
                        PlayFabId = playFabId,
                        VirtualCurrency = CURRENCYCASH
                    });
                }

                return new OkObjectResult(ResultMission);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"An error occurred: {ex.Message}", ex);
                return new BadRequestObjectResult($"Something Went Wrong! {ex.Message}");
            }
        }
    }
}