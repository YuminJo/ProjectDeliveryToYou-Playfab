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
    public class DeliveryStateData
    {
        public string Character { get; set; }
        public bool Active { get; set; }
        public int EndTime { get; set; }
        public int Star { get; set; }
        public int PaymentValue { get; set; }
        public int AdditionalTips { get; set; }
        public int MisDeliveries { get; set; }
    }

    public static class OnDeliveryController
    {
        private static async Task UpdateUserReadOnlyDataAsync<T>(PlayFabServerInstanceAPI serverApi, string playFabId, string key, T data)
        {
            var jsonData = PlayFabSimpleJson.SerializeObject(data);
            await serverApi.UpdateUserReadOnlyDataAsync(new UpdateUserDataRequest
            {
                PlayFabId = playFabId,
                Data = new Dictionary<string, string> { { key, jsonData } }
            });
        }

        [FunctionName("OnDeliveryController")]
        public static async Task<dynamic> OnDeliveryControllers(
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

                if (args == null || !args.ContainsKey("DeliveryNumber"))
                {
                    return new BadRequestObjectResult("Invalid request data.");
                }

                string deliveryNumber = args["DeliveryNumber"].ToString();
                string currentDelivery = $"DeliveryState{deliveryNumber}";

                var getUserInfoResponse = await serverApi.GetPlayerCombinedInfoAsync(new GetPlayerCombinedInfoRequest
                {
                    PlayFabId = playFabId,
                    InfoRequestParameters = new GetPlayerCombinedInfoRequestParams()
                    {
                        GetUserReadOnlyData = true
                    }
                });

                var getUserInfoData = getUserInfoResponse.Result;
                var getUserDeliveryData = getUserInfoData.InfoResultPayload.UserReadOnlyData.GetValueOrDefault(currentDelivery)?.Value;

                DeliveryStateData deliveryStateData = PlayFabSimpleJson.DeserializeObject<DeliveryStateData>(getUserDeliveryData);

                if (!deliveryStateData.Active)
                {
                    return new BadRequestObjectResult("Null Data Exception");
                }

                int currentDeliveryTime = deliveryStateData.EndTime - ((int)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds);

                if (deliveryStateData.PaymentValue > 0 && deliveryStateData.EndTime != -1 && currentDeliveryTime < 1)
                {
                    // Delivery End

                    // ADD VIRTUAL CURRENCY
                    await serverApi.AddUserVirtualCurrencyAsync(new AddUserVirtualCurrencyRequest
                    {
                        Amount = deliveryStateData.PaymentValue + deliveryStateData.AdditionalTips - deliveryStateData.MisDeliveries,
                        PlayFabId = playFabId,
                        VirtualCurrency = "GD"
                    });

                    //플레이어 통계 최신화
                    var request = new UpdatePlayerStatisticsRequest
                    {
                        PlayFabId = playFabId,
                        Statistics = new List<StatisticUpdate>()
                        {
                            new StatisticUpdate
                            {
                                StatisticName = "DELIVERY",
                                Value = 1
                            },
                            new StatisticUpdate
                            {
                                StatisticName = "DAILYMISSION_DELIVERY",
                                Value = 1
                            },
                            new StatisticUpdate
                            {
                                StatisticName = "STOREREPUTATION",
                                Value = 2
                            }
                        }
                    };

                    await serverApi.UpdatePlayerStatisticsAsync(request);

                    DeliveryStateData updateDeliveryState = new()
                    {
                        Character = "none",
                        Active = true,
                        EndTime = -1,
                        Star = 3,
                        PaymentValue = 0,
                        AdditionalTips = 0,
                        MisDeliveries = 0
                    };

                    await UpdateUserReadOnlyDataAsync(serverApi, playFabId, currentDelivery, updateDeliveryState);
                    return new OkObjectResult(new { success = true });
                }
                else
                {
                    if (!args.ContainsKey("CharacterName") || !args.ContainsKey("FoodNumber") ||
                    !args.ContainsKey("DeliveryLocation") || !args.ContainsKey("CustomerDeliveryLocation") || !args.ContainsKey("CustomerDeliveryFood"))
                    {
                        return new BadRequestObjectResult("Invalid request data.");
                    }

                    string characterName = args["CharacterName"].ToString();
                    string foodNumber = args["FoodNumber"].ToString();
                    string customerDeliveryFood = args["CustomerDeliveryFood"].ToString();
                    int customerDeliveryLocation = (int)args["CustomerDeliveryLocation"];
                    int deliveryLocation = (int)args["DeliveryLocation"];

                    string currentFood = $"FoodState{foodNumber}";

                    var getUserFoodData = getUserInfoData.InfoResultPayload.UserReadOnlyData.GetValueOrDefault(currentFood)?.Value;
                    FoodStateData foodStateData = PlayFabSimpleJson.DeserializeObject<FoodStateData>(getUserFoodData);

                    if (foodStateData.FoodName != null && deliveryStateData.Character == "none" && deliveryStateData.PaymentValue == 0 && deliveryStateData.EndTime == -1)
                    {
                        //배달 시작
                        bool IsLocationCorrect = (customerDeliveryLocation == deliveryLocation);
                        bool IsFoodCorrect = (foodStateData.FoodName == customerDeliveryFood);

                        deliveryStateData.Character = characterName;
                        deliveryStateData.EndTime = (int)((DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds + 300 + (deliveryLocation * 5 * 60));
                        deliveryStateData.Star = 3 + (IsLocationCorrect ? 0 : -1) + (IsFoodCorrect ? 0 : -1);
                        deliveryStateData.PaymentValue = (int)foodStateData.Earning;
                        deliveryStateData.AdditionalTips = foodStateData.Earning * 3 + (foodStateData.Earning * deliveryLocation * 3) - deliveryStateData.PaymentValue;
                        deliveryStateData.MisDeliveries += IsLocationCorrect ? 0 : (int)((deliveryStateData.PaymentValue + deliveryStateData.AdditionalTips) * 0.3f);
                        deliveryStateData.MisDeliveries += IsFoodCorrect ? 0 : (int)((deliveryStateData.PaymentValue + deliveryStateData.AdditionalTips) * 0.3f);

                        foodStateData.FoodName = "none";
                        foodStateData.OvenUse = false;
                        foodStateData.OvenEndTime = -1;
                        foodStateData.OvenTime = -1;
                        foodStateData.Earning = 0;

                        await UpdateUserReadOnlyDataAsync(serverApi, playFabId, currentDelivery, deliveryStateData);
                        await UpdateUserReadOnlyDataAsync(serverApi, playFabId, currentFood, foodStateData);
                    }
                }

                return new OkObjectResult(new
                {
                    star = deliveryStateData.Star.ToString(),
                    payment = deliveryStateData.PaymentValue.ToString(),
                    additional = deliveryStateData.AdditionalTips.ToString(),
                    misdeliveries = deliveryStateData.MisDeliveries.ToString()
                });
            }
            catch (Exception ex)
            {
                return new BadRequestObjectResult($"Something Went Wrong! {ex.Message}");
            }
        }
    }
}