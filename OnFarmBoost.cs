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
    public static class OnFarmBoost
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

        private static async Task<string> ConsumeItemAsync(FunctionExecutionContext<dynamic> context, dynamic itemId, PlayFabServerInstanceAPI serverApi)
        {
            var result = await serverApi.ConsumeItemAsync(new PlayFab.ServerModels.ConsumeItemRequest()
            {
                PlayFabId = context.CallerEntityProfile.Lineage.MasterPlayerAccountId,
                ItemInstanceId = itemId,
                ConsumeCount = 1
            });

            return result.Result.ItemInstanceId;
        }

        [FunctionName("OnFarmBoost")]
        public static async Task<dynamic> OnFarmBoosts(
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

            int FarmNumber = (int)args["FarmNumber"];
            string currentFarm = "FarmState" + FarmNumber.ToString();

            try
            {
                var getUserInfoResponse = await serverApi.GetPlayerCombinedInfoAsync(new GetPlayerCombinedInfoRequest
                {
                    PlayFabId = playFabId,
                    InfoRequestParameters = new GetPlayerCombinedInfoRequestParams()
                    {
                        GetUserInventory = true,
                        GetUserReadOnlyData = true
                    }
                });

                var getUserInfoData = getUserInfoResponse.Result;
                var getUserData = getUserInfoData.InfoResultPayload.UserReadOnlyData.GetValueOrDefault(currentFarm)?.Value;
                var getUserFarmItem = getUserInfoData.InfoResultPayload.UserInventory
                .FirstOrDefault(item => item.ItemId == "BoostFarmTime");

                FarmStateData farmStateData = PlayFabSimpleJson.DeserializeObject<FarmStateData>(getUserData);

                int farmEndTime = farmStateData.FarmEndTime;
                int currentFarmTime = farmEndTime - ((int)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds);
                if (farmStateData == null || !farmStateData.FarmActive) return new BadRequestObjectResult("Farm data not found.");
                if (currentFarmTime <= 0) return new BadRequestObjectResult("Farm Already End!");
                if (getUserFarmItem.RemainingUses <= 0) return new BadRequestObjectResult("No Item Data");

                // 아이템 소비
                var result = await ConsumeItemAsync(context, getUserFarmItem.ItemInstanceId, serverApi);

                var updatefarmStateData = new FarmStateDataValue(true, 0, farmStateData.FarmItem, farmStateData.FarmItemAmount);
                await UpdateUserReadOnlyDataAsync(serverApi, playFabId, currentFarm, updatefarmStateData);
                return new OkObjectResult(new());
            }
            catch (Exception ex)
            {
                return new BadRequestObjectResult($"Something Went Wrong! {ex.Message}");
            }
        }
    }
}
