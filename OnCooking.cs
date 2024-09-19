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
    public class RecipeDetailsData
    {
        public int templateid;
        public string foodtype;
        public string ingredient1;
        public string ingredient2;
        public string ingredient3;
        public string ingredient4;
        public bool ovenuse;
        public int oventime;
        public int deliverytime;
    }

    public class FoodStateData
    {
        public string FoodName;
        public bool OvenUse;
        public int OvenEndTime;
        public int OvenTime;
        public int Earning;
    }

    public static class OnCooking
    {
        record FoodStateDataValue(string FoodName, bool OvenUse, int OvenEndTime, int OvenTime, int Earning);

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

        [FunctionName("OnCooking")]
        public static async Task<dynamic> OnCookings(
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

            if (args == null || args["RecipeName"] == null || args["FoodNumber"] == null)
            {
                return new BadRequestObjectResult("Invalid request data.");
            }

            string recipeName = args["RecipeName"].ToString();
            string foodNumber = args["FoodNumber"].ToString();
            string currentFood = "FoodState" + foodNumber;

            //현재 사용한 재료 등록
            List<string> IngredientList = new List<string>();

            for (int i = 1; i <= 4; i++)
            {
                string ingredient = args["Ingredient" + i]?.ToString();
                if (!string.IsNullOrEmpty(ingredient))
                {
                    IngredientList.Add(ingredient);
                }
            }

            //인벤에 등록된 재료가 2개 미만
            if (IngredientList.Count < 2) return new BadRequestObjectResult("Ingredient Count Error");
            try
            {
                var getUserInventory = await serverApi.GetUserInventoryAsync(new GetUserInventoryRequest()
                {
                    PlayFabId = playFabId,
                });

                if (recipeName == "이상한요리")
                {
                    List<ItemInstance> items = new();

                    //재료 인벤토리 여부 확인
                    foreach (var ingredientName in IngredientList)
                    {
                        var userItem = getUserInventory.Result.Inventory.FirstOrDefault(item => item.ItemId == ingredientName);

                        if (userItem == null)
                        {
                            return new BadRequestObjectResult("User doesn't have the specified item in their inventory.");
                        }

                        items.Add(userItem);
                    }
                    foreach (var ConsumeItem in items)
                    {
                        if (ConsumeItem.RemainingUses == null) continue;
                        await ConsumeItemAsync(context, ConsumeItem.ItemInstanceId, serverApi);
                    }

                    if (items.Count < 2) return new BadRequestObjectResult("Ingredient Count Error");

                    //플레이어 타이틀 데이터 업데이트
                    var updateStrangeFoodStateData = new FoodStateDataValue("이상한요리", false, -1, 0, 10);
                    await UpdateUserReadOnlyDataAsync(serverApi, playFabId, currentFood, updateStrangeFoodStateData);

                    return new OkObjectResult(new());
                }

                var getUserData = await serverApi.GetUserReadOnlyDataAsync(new GetUserDataRequest
                {
                    PlayFabId = playFabId,
                    Keys = new List<string> { "Recipes", currentFood }
                });

                var getCatalogItems = await serverApi.GetCatalogItemsAsync(new GetCatalogItemsRequest()
                {
                    CatalogVersion = "Recipes"
                });

                var getIngredientCatalogItems = await serverApi.GetCatalogItemsAsync(new GetCatalogItemsRequest()
                {
                    CatalogVersion = "Main"
                });

                //레시피에 등록된 재료 목록
                var ingredientNames = new List<string>();

                //음식 중복 여부 검증
                var foodStateJson = getUserData.Result.Data.ContainsKey(currentFood) ? getUserData.Result.Data[currentFood].Value : null;
                FoodStateData foodStateData = PlayFabSimpleJson.DeserializeObject<FoodStateData>(foodStateJson);
                if (foodStateData == null || foodStateData.FoodName != "none")
                {
                    return new BadRequestObjectResult("The food is already in your inventory.");
                }

                //카탈로그 레시피 여부 검증
                var recipeItem = getCatalogItems.Result.Catalog.FirstOrDefault(item => item.ItemId == recipeName);
                if (recipeItem == null)
                {
                    return new BadRequestObjectResult("Invalid Recipe name.");
                }

                //데이터에 등록된 재료
                var customData = PlayFabSimpleJson.DeserializeObject<Dictionary<string, object>>(recipeItem.CustomData);
                bool ovenUse = customData.TryGetValue("ovenuse", out var ovenUseValue) && Convert.ToBoolean(ovenUseValue);
                int ovenTime = customData.TryGetValue("oventime", out var ovenTimeValue) ? Convert.ToInt32(ovenTimeValue) : 9999;
                int templateId = customData.TryGetValue("templateid", out var templateIdValue) ? Convert.ToInt32(templateIdValue) : -1;

                for (int i = 1; i <= 4; i++)
                {
                    if (customData.TryGetValue($"ingredient{i}", out var ingredientName))
                    {
                        ingredientNames.Add(ingredientName.ToString());
                    }
                }

                //레시피 획득 여부 검증
                var recipeStateJson = getUserData.Result.Data.ContainsKey("Recipes") ? getUserData.Result.Data["Recipes"].Value : null;
                var recipeStateData = PlayFabSimpleJson.DeserializeObject<Dictionary<string, bool>>(recipeStateJson);

                if (recipeStateData != null && recipeStateData.TryGetValue(templateId.ToString(), out var recipeEnabled) && !recipeEnabled)
                {
                    return new BadRequestObjectResult("The recipe is not active.");
                }

                int EarningMoney = 0;
                //재료 인벤토리 여부 확인
                foreach (var ingredientName in ingredientNames)
                {
                    Console.WriteLine(ingredientName);
                    var userItem = getUserInventory.Result.Inventory.FirstOrDefault(item => item.ItemId == ingredientName);
                    if (userItem == null)
                    {
                        return new BadRequestObjectResult("User doesn't have the specified item in their inventory.");
                    }
                    //최종 획득할 금액
                    EarningMoney += (int)getIngredientCatalogItems.Result.Catalog.FirstOrDefault(item => item.ItemId == userItem.ItemId).VirtualCurrencyPrices["GD"];
                    //아이템 소비
                    if (userItem.RemainingUses == null) continue;
                    await ConsumeItemAsync(context, userItem.ItemInstanceId, serverApi);
                }
                //플레이어 타이틀 데이터 업데이트
                var updateFoodStateData = new FoodStateDataValue(recipeName, ovenUse, -1, ovenTime, EarningMoney);
                await UpdateUserReadOnlyDataAsync(serverApi, playFabId, currentFood, updateFoodStateData);

                //플레이어 통계 최신화
                //재료 검증해서 Tag Special이면 True로 하고 하면 될 것 같은데..?
                int dailymakingfood = 0;
                int makingfood = 0;
                int makingspecialfood = 0;

                var request = new UpdatePlayerStatisticsRequest
                {
                    PlayFabId = playFabId,
                    Statistics = new List<StatisticUpdate>()
                };

                dailymakingfood += 1;
                request.Statistics.Add(new StatisticUpdate
                {
                    StatisticName = "DAILYMISSION_MAKINGFOOD",
                    Value = 1
                });

                makingfood += 1;
                request.Statistics.Add(new StatisticUpdate
                {
                    StatisticName = "MAKINGFOOD",
                    Value = 1
                });

                await serverApi.UpdatePlayerStatisticsAsync(request);

                return new OkObjectResult(new { DAILYMISSION_MAKINGFOOD = dailymakingfood, MAKINGFOOD = makingfood });
            }
            catch (Exception ex)
            {
                return new BadRequestObjectResult($"Something Went Wrong! {ex.Message}");
            }
        }
    }
}
