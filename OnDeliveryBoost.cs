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
    public static class OnDeliveryBoost
    {
        record DeliveryStateDataValue(string Character, bool Active, int EndTime, int Star, int PaymentValue, int AdditionalTips, int MisDeliveries);
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

        [FunctionName("OnDeliveryBoost")]
        public static async Task<dynamic> OnDeliveryBoosts(
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

            if (args == null || args["DeliveryNumber"] == null)
            {
                return new BadRequestObjectResult("Invalid request data.");
            }

            int DeliveryNumber = (int)args["DeliveryNumber"];
            string currentDelivery = "DeliveryState" + DeliveryNumber.ToString();

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
                var getUserData = getUserInfoData.InfoResultPayload.UserReadOnlyData.GetValueOrDefault(currentDelivery)?.Value;
                var getUserDeliveryItem = getUserInfoData.InfoResultPayload.UserInventory
                .FirstOrDefault(item => item.ItemId == "BoostDeliveryTime");

                DeliveryStateData deliveryStateData = PlayFabSimpleJson.DeserializeObject<DeliveryStateData>(getUserData);

                int deliveryEndTime = deliveryStateData.EndTime;
                int currentDeliveryTime = deliveryEndTime - ((int)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds);
                if (deliveryStateData == null || !deliveryStateData.Active) return new BadRequestObjectResult("data not found.");
                if (currentDeliveryTime <= 0) return new BadRequestObjectResult("Already End!");
                if (getUserDeliveryItem.RemainingUses <= 0) return new BadRequestObjectResult("No Item Data");

                // 아이템 소비
                var result = await ConsumeItemAsync(context, getUserDeliveryItem.ItemInstanceId, serverApi);

                var updateDeliveryStateData = new DeliveryStateDataValue
                (deliveryStateData.Character, true, 0, deliveryStateData.Star, deliveryStateData.PaymentValue, deliveryStateData.AdditionalTips, deliveryStateData.MisDeliveries);
                await UpdateUserReadOnlyDataAsync(serverApi, playFabId, currentDelivery, updateDeliveryStateData);
                return new OkObjectResult(new());
            }
            catch (Exception ex)
            {
                return new BadRequestObjectResult($"Something Went Wrong! {ex.Message}");
            }
        }
    }
}
