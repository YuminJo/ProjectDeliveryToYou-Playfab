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
using Microsoft.WindowsAzure.Storage.Blob;
using PlayFab.MultiplayerModels;
using System.Linq;

namespace DeliveryToYou.Function
{
    public static class OnPlayerInit
    {
        record OvenStateData(bool OvenActive, int OvenEndTime, string OvenItem);
        record FarmStateData(bool FarmActive, int FarmEndTime, string FarmItem, int FarmItemAmount);
        record DeliveryStateData(string Character, bool Active, int EndTime, int Star, int PaymentValue, int AdditionalTips, int MisDeliveries);
        record FoodStateData(string FoodName, bool OvenUse, int OvenEndTime, int OvenTime, int Earning);

        private const string FirstLoginKey = "FirstLogin";

        private static async Task UpdateUserReadOnlyDataAsync<T>(PlayFabServerInstanceAPI serverApi, string playFabId, string key, T data)
        {
            var jsonData = PlayFabSimpleJson.SerializeObject(data);
            await serverApi.UpdateUserReadOnlyDataAsync(new UpdateUserDataRequest
            {
                PlayFabId = playFabId,
                Data = new Dictionary<string, string> { { key, jsonData } }
            });
        }

        private static async Task InitializeUserDataAsync(PlayFabServerInstanceAPI serverApi, string playFabId)
        {
            // Initialize TutorialData
            var tutorialData = new Dictionary<string, bool>
            {
                { "MainTutorial", false }
            };

            await UpdateUserReadOnlyDataAsync(serverApi, playFabId, "Tutorial", tutorialData);

            // Initialize DefaultData
            var defaultData = new Dictionary<string, string>
            {
                { "ShelfCount", "1" },
                { "FarmCount", "3" },
                { "Oven", "false"}
            };

            await serverApi.UpdateUserReadOnlyDataAsync(new UpdateUserDataRequest
            {
                PlayFabId = playFabId,
                Data = defaultData
            });

            // Initialize DeliveryData
            var deliveryStateData = new DeliveryStateData("none", true, -1, 3, 0, 0, 0);
            await UpdateUserReadOnlyDataAsync(serverApi, playFabId, "DeliveryState0", deliveryStateData);
            await UpdateUserReadOnlyDataAsync(serverApi, playFabId, "DeliveryState1", deliveryStateData);
            deliveryStateData = new DeliveryStateData("none", false, -1, 3, 0, 0, 0);
            await UpdateUserReadOnlyDataAsync(serverApi, playFabId, "DeliveryState2", deliveryStateData);

            // Initialize OvenData
            var ovenStateData = new OvenStateData(false, -1, "none");
            await UpdateUserReadOnlyDataAsync(serverApi, playFabId, "OvenData", ovenStateData);

            // Initialize FarmData
            for (int i = 1; i <= 3; i++)
            {
                var farmStateData = new FarmStateData(true, -1, "none", 0);
                await UpdateUserReadOnlyDataAsync(serverApi, playFabId, $"FarmState{i}", farmStateData);
            }

            // Initialize FarmData
            for (int i = 4; i <= 9; i++)
            {
                var farmStateData = new FarmStateData(false, -1, "none", 0);
                await UpdateUserReadOnlyDataAsync(serverApi, playFabId, $"FarmState{i}", farmStateData);
            }

            // Initialize CurrentFoodData
            for (int i = 0; i <= 2; i++)
            {
                var foodStateData = new FoodStateData("none", false, -1, -1, 0);
                await UpdateUserReadOnlyDataAsync(serverApi, playFabId, $"FoodState{i}", foodStateData);
            }

            // Initialize RecipeData
            string[] recipeData = { "얼음물", "모닝빵", "쌀경단" };
            string json = PlayFabSimpleJson.SerializeObject(recipeData);
            await UpdateUserReadOnlyDataAsync(serverApi, playFabId, "Recipes", recipeData);

            //플레이어 통계 최신화
            var request = new UpdatePlayerStatisticsRequest
            {
                PlayFabId = playFabId,
                Statistics = new List<StatisticUpdate>()
            };

            request.Statistics.Add(new StatisticUpdate
            {
                StatisticName = "CURRENTSTORY",
                Value = 1
            });

            await serverApi.UpdatePlayerStatisticsAsync(request);
        }

        [FunctionName("OnPlayerInit")]
        public static async Task<dynamic> CheckFirstLogin(
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

            var firstlogin = await serverApi.GetPlayerStatisticsAsync(new GetPlayerStatisticsRequest
            {
                PlayFabId = playFabId,
            });

            var playerStatistic = firstlogin.Result.Statistics.FirstOrDefault(item => item.StatisticName == "CURRENTSTORY");
            if (playerStatistic == null)
            {
                await InitializeUserDataAsync(serverApi, playFabId);
            }

            return new OkObjectResult(new { success = true });
        }
    }
}