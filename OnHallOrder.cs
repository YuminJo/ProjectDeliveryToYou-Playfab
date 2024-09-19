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

namespace DeliveryToYou.Function
{
    public static class OnHallOrder
    {
        record FoodStateDataValue(string FoodName, bool OvenUse, int OvenEndTime, int OvenTime, int Earning);
        private const string CurrencyName = "GD";
        private const int MinRandomMultiplier = 13;
        private const int MaxRandomMultiplier = 16;
        private const int DefaultMultiplier = 10;
        private const string NoneFoodName = "none";

        private static readonly Random RandomGenerator = new Random();

        private static async Task UpdateUserReadOnlyDataAsync<T>(PlayFabServerInstanceAPI serverApi, string playFabId, string key, T data)
        {
            var jsonData = PlayFabSimpleJson.SerializeObject(data);
            await serverApi.UpdateUserReadOnlyDataAsync(new UpdateUserDataRequest
            {
                PlayFabId = playFabId,
                Data = new Dictionary<string, string> { { key, jsonData } }
            });
        }

        [FunctionName("OnHallOrder")]
        public static async Task<dynamic> OnHallOrders(
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

            if (args == null || args["CharacterOrderRecipeName"] == null || args["OrderRecipeName"] == null || args["FoodNumber"] == null)
            {
                return new BadRequestObjectResult("Invalid request data.");
            }

            string CharacterOrderRecipeName = args["CharacterOrderRecipeName"].ToString();
            string OrderRecipeName = args["OrderRecipeName"].ToString();
            string foodNumber = args["FoodNumber"].ToString();
            string currentFood = "FoodState" + foodNumber;
            //string currentCharacter = args["CurrentCharacter"].Tostring();
            //나중에 캐릭터 데이터도 받아와야 하지만 지금은 받지 않는다..


            try
            {
                var getUserFoodStateData = await serverApi.GetUserReadOnlyDataAsync(new GetUserDataRequest
                {
                    PlayFabId = playFabId,
                    Keys = new List<string> { currentFood }
                });

                //음식 관련 검증
                var foodStateJson = getUserFoodStateData.Result.Data.ContainsKey(currentFood) ? getUserFoodStateData.Result.Data[currentFood].Value : null;
                FoodStateData foodStateData = PlayFabSimpleJson.DeserializeObject<FoodStateData>(foodStateJson);

                if (foodStateData == null || foodStateData.FoodName == "none" || foodStateData.OvenEndTime != -1)
                {
                    return new BadRequestObjectResult("Food Error!");
                }

                int FoodPrice = foodStateData.Earning;
                //음식이 같다.
                if (foodStateData.FoodName == CharacterOrderRecipeName)
                {
                    //설정된 배율에 따라 랜덤 돈 
                    int randomMultiplier = RandomGenerator.Next(MinRandomMultiplier, MaxRandomMultiplier);
                    FoodPrice = FoodPrice * randomMultiplier / DefaultMultiplier;
                }
                else
                {
                    FoodPrice = FoodPrice * 8 / DefaultMultiplier;
                }

                var updatefoodStateData = new FoodStateDataValue("none", false, -1, -1, 0);
                await UpdateUserReadOnlyDataAsync(serverApi, playFabId, currentFood, updatefoodStateData);

                // ADD VIRTUAL CURRENCY
                await serverApi.AddUserVirtualCurrencyAsync(new AddUserVirtualCurrencyRequest
                {
                    Amount = FoodPrice,
                    PlayFabId = playFabId,
                    VirtualCurrency = CurrencyName
                });

                //플레이어 통계 최신화
                var request = new UpdatePlayerStatisticsRequest
                {
                    PlayFabId = playFabId,
                    Statistics = new List<StatisticUpdate>()
                };

                request.Statistics.Add(new StatisticUpdate
                {
                    StatisticName = "WEEKLYMISSION_SERVING",
                    Value = 1
                });

                request.Statistics.Add(new StatisticUpdate
                {
                    StatisticName = "SERVING",
                    Value = 1
                });

                request.Statistics.Add(new StatisticUpdate
                {
                    StatisticName = "STOREREPUTATION",
                    Value = 1
                });

                await serverApi.UpdatePlayerStatisticsAsync(request);

                return new OkObjectResult(new { ResultMoney = FoodPrice, WEEKLYMISSION_SERVING = 1, SERVING = 1 });
            }
            catch (Exception ex)
            {
                log.LogError($"An error occurred: {ex.Message}", ex);
                return new BadRequestObjectResult($"Something Went Wrong! {ex.Message}");
            }
        }
    }
}