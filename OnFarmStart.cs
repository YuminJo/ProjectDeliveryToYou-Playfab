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
    public class FarmStateData
    {
        public bool FarmActive;
        public int FarmEndTime;
        public string FarmItem;
        public int FarmItemAmount;
    }

    public static class OnFarmStart
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

        [FunctionName("OnFarmStart")]
        public static async Task<dynamic> OnFarmStarts(
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

            if (args == null || args["SeedName"] == null || args["FarmNumber"] == null)
            {
                return new BadRequestObjectResult("Invalid request data.");
            }

            string SeedName = args["SeedName"].ToString();
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

                var getUserInvenTask = serverApi.GetUserInventoryAsync(new GetUserInventoryRequest()
                {
                    PlayFabId = playFabId,
                });
                tasks.Add(getUserInvenTask);

                var getCatalogItemsTask = serverApi.GetCatalogItemsAsync(new GetCatalogItemsRequest()
                {
                    CatalogVersion = "Seeds"
                });
                tasks.Add(getCatalogItemsTask);

                await Task.WhenAll(tasks);

                var getUserData = getUserDataTask.Result;
                var getUserInven = getUserInvenTask.Result;
                var getCatalogItems = getCatalogItemsTask.Result;

                // 검증
                var userItem = getUserInven.Result.Inventory.Find(item => item.ItemId == SeedName);
                if (userItem == null)
                {
                    return new BadRequestObjectResult("User doesn't have the specified item in their inventory.");
                }
                if (userItem.RemainingUses <= 0)
                {
                    return new BadRequestObjectResult("The specified item has no remaining uses.");
                }

                var FarmJson = getUserData.Result.Data[CurrentFarm]?.Value;
                FarmStateData farmStateData = PlayFabSimpleJson.DeserializeObject<FarmStateData>(FarmJson);

                if (FarmJson == null)
                {
                    return new BadRequestObjectResult("Farm data not found.");
                }

                bool farmActive = false;
                int farmEndTime = 99999;

                if (farmStateData != null)
                {
                    farmActive = farmStateData.FarmActive;
                    farmEndTime = farmStateData.FarmEndTime;
                }

                if (!farmActive)
                {
                    return new BadRequestObjectResult("The Farm is not Active.");
                }
                if (farmEndTime != -1)
                {
                    return new BadRequestObjectResult("The Farm is already active.");
                }

                var getCatalogItem = getCatalogItems.Result.Catalog.Find(item => item.ItemId == SeedName);
                if (getCatalogItem == null)
                {
                    return new BadRequestObjectResult("Invalid seed name.");
                }

                // 아이템 소비
                var result = await ConsumeItemAsync(context, userItem.ItemInstanceId, serverApi);

                // Time 전송
                var customdata = PlayFabSimpleJson.DeserializeObject<Dictionary<string, object>>(getCatalogItem.CustomData);

                if (customdata.TryGetValue("SeedTime", out object seedTimeValue))
                {
                    int seedTime = Convert.ToInt32(seedTimeValue);
                    farmEndTime = (int)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds + seedTime - 1;
                    string itemname = SeedName.Replace("seed", "");
                    Random rand = new Random();
                    int randomAmount = rand.Next(2) + 2;

                    var updatefarmStateData = new FarmStateDataValue(true, farmEndTime, itemname, randomAmount);

                    await UpdateUserReadOnlyDataAsync(serverApi, playFabId, CurrentFarm, updatefarmStateData);

                    return new { seedtime = seedTimeValue.ToString(), amount = randomAmount.ToString() };
                }

                return new BadRequestObjectResult("Finish Failed!");
            }
            catch (Exception ex)
            {
                return new BadRequestObjectResult($"Something Went Wrong! {ex.Message}");
            }
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
    }
}
