using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using PlayFab.Samples;
using System.Collections.Generic;
using PlayFab;
using PlayFab.Json;
using System.Linq;
using PlayFab.ServerModels;

namespace DeliveryToYou.Function
{
    public static class OnFarmEnd
    {
        record FarmStateDataValue(bool FarmActive, int FarmEndTime, string FarmItem, int FarmItemAmount);

        private static async Task UpdateUserReadOnlyDataAsync<T>(PlayFabServerInstanceAPI serverApi, string playFabId, string key, T data)
        {
            var jsonData = PlayFabSimpleJson.SerializeObject(data);
            await serverApi.UpdateUserReadOnlyDataAsync(new UpdateUserDataRequest
            {
                PlayFabId = playFabId,
                Data = new Dictionary<string, string> { { key, jsonData } }
            });
        }

        [FunctionName("OnFarmEnd")]
        public static async Task<dynamic> OnFarmEnds(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", "post", Route = null)] HttpRequest req,
                ILogger log)
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

            if (args == null || args["FarmNumber"] == null)
            {
                return new BadRequestObjectResult("Invalid request data.");
            }

            string FarmNumber = args["FarmNumber"].ToString();
            string CurrentFarm = "FarmState" + FarmNumber;

            try
            {
                var tasks = new List<Task>();

                var getUserDataTask = serverApi.GetUserReadOnlyDataAsync(new GetUserDataRequest
                {
                    PlayFabId = playFabId,
                    Keys = new List<string>
                    {
                        CurrentFarm
                    }
                });
                tasks.Add(getUserDataTask);

                await Task.WhenAll(tasks);

                var getUserData = getUserDataTask.Result;

                //검증
                var FarmJson = getUserData.Result.Data[CurrentFarm]?.Value;

                if (FarmJson == null)
                {
                    return new BadRequestObjectResult("Farm data not found.");
                }

                FarmStateData farmStateData = PlayFabSimpleJson.DeserializeObject<FarmStateData>(FarmJson);

                bool farmActive = false;
                int farmEndTime = -1;
                int currentFarmTime = -1;
                string farmItemname = null;
                int random_amount = 0;

                if (farmStateData != null)
                {
                    farmActive = farmStateData.FarmActive;
                    farmEndTime = farmStateData.FarmEndTime;
                    currentFarmTime = farmEndTime - ((int)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds);
                    farmItemname = farmStateData.FarmItem;
                    random_amount = farmStateData.FarmItemAmount;
                }

                if (!farmActive) return new BadRequestObjectResult("The Farm is not Active.");
                if (farmItemname == null) return new BadRequestObjectResult("No Available Farm!");
                if (farmEndTime == -1) return new BadRequestObjectResult("The Farm is not active.");
                if (currentFarmTime == -1) return new BadRequestObjectResult("The Farm is not active.");
                if (currentFarmTime > 2) return new BadRequestObjectResult("Not End Farm!");

                List<string> argItemId = new List<string>();

                for (int i = 0; i < random_amount; i++)
                {
                    argItemId.Add(farmItemname);
                }

                //유저 데이터 업데이트
                var updatetasks = new List<Task>();

                var addedUserItem = serverApi.GrantItemsToUserAsync(new PlayFab.ServerModels.GrantItemsToUserRequest()
                {
                    ItemIds = argItemId,
                    PlayFabId = playFabId
                });
                updatetasks.Add(addedUserItem);

                var updatefarmStateData = new FarmStateDataValue(true, -1, "none", 0);

                await UpdateUserReadOnlyDataAsync(serverApi, playFabId, CurrentFarm, updatefarmStateData);
                //플레이어 통계 최신화
                var request = new UpdatePlayerStatisticsRequest
                {
                    PlayFabId = playFabId,
                    Statistics = new List<StatisticUpdate>()
                };

                request.Statistics.Add(new StatisticUpdate
                {
                    StatisticName = "DAILYMISSION_FARMING",
                    Value = 1
                });

                request.Statistics.Add(new StatisticUpdate
                {
                    StatisticName = "FARMING",
                    Value = 1
                });

                await serverApi.UpdatePlayerStatisticsAsync(request);

                return new { itemname = farmItemname, itemamount = random_amount.ToString() };
            }
            catch (Exception ex)
            {
                return new BadRequestObjectResult($"Something Went Wrong! {ex.Message}");
            }
        }
    }
}
