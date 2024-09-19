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
    public static class OnAchievement
    {
        public class Achievement
        {
            public int num;
            public string name;
            public string description;
            public Typeclass requirement;
            public Typeclass reward;
        }

        public class Typeclass
        {
            public string type;
            public List<string> target;
            public int amount;
        }

        public class Root
        {
            public List<Achievement> CURRENTSTORY { get; set; }
            public List<Achievement> TUTORIAL { get; set; }
            public List<Achievement> STOREREPUTATION { get; set; }
        }

        public enum ATType
        {
            AT_CURRENTSTORY,
            AT_TUTORIAL,
            AT_STOREREPUTATION
        }
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

        //업데이트 방법
        //ATType의 경우는 대분류의 카테고리이다. 이는 분류가 추가되면 추가해야한다.
        [FunctionName("OnAchievement")]
        public static async Task<dynamic> OnAchievements(
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

            if (args == null || args["CatalogType"] == null)
            {
                return new BadRequestObjectResult("Invalid request data.");
            }

            string CatalogType = args["CatalogType"].ToString();
            ATType aTType = new ATType();

            try
            {
                var getUserInfoResponse = await serverApi.GetPlayerCombinedInfoAsync(new GetPlayerCombinedInfoRequest
                {
                    PlayFabId = playFabId,
                    InfoRequestParameters = new GetPlayerCombinedInfoRequestParams()
                    {
                        GetPlayerStatistics = true,
                        GetUserReadOnlyData = true,
                        GetTitleData = true
                    }
                });

                var getUserInfoData = getUserInfoResponse.Result;
                var achieveJson = getUserInfoData.InfoResultPayload.TitleData["Achieve"];
                Root root = JsonConvert.DeserializeObject<Root>(achieveJson);

                Achievement CurrentAchievement = null;
                //카탈로그 검증
                switch (CatalogType)
                {
                    case "AT_CURRENTSTORY":
                        int CurrentStoryNum = getUserInfoData.InfoResultPayload.PlayerStatistics.FirstOrDefault(item => item.StatisticName == "AT_CURRENTSTORY")?.Value ?? 0;
                        CurrentAchievement = root.CURRENTSTORY[CurrentStoryNum];
                        aTType = ATType.AT_CURRENTSTORY;
                        break;

                    case "AT_TUTORIAL":
                        int CurrentTutorialNum = getUserInfoData.InfoResultPayload.PlayerStatistics.FirstOrDefault(item => item.StatisticName == "AT_TUTORIAL")?.Value ?? 0;
                        CurrentAchievement = root.TUTORIAL[CurrentTutorialNum];
                        aTType = ATType.AT_TUTORIAL;
                        break;

                    case "AT_STOREREPUTATION":
                        int CurrentStoreReputation = getUserInfoData.InfoResultPayload.PlayerStatistics.FirstOrDefault(item => item.StatisticName == "AT_STOREREPUTATION")?.Value ?? 0;
                        CurrentAchievement = root.STOREREPUTATION[CurrentStoreReputation];
                        aTType = ATType.AT_STOREREPUTATION;
                        break;

                    default:
                        return new BadRequestObjectResult("No Catalog!");
                }

                var requirement = CurrentAchievement.requirement;
                //현재 조건 검증
                switch (CurrentAchievement.requirement.type)
                {
                    case "recipe":
                        //이미 검증한 레시피인지
                        if (getUserInfoData.InfoResultPayload.UserReadOnlyData.GetValueOrDefault("StoryRecipe")?.Value == CurrentAchievement.name)
                        {
                            break;
                        }
                        var requirementrecipe = requirement.target;

                        //몇번째 음식인지
                        String IsRecipeNum0 = "FoodState" + args["FoodNum0"].Tostring();
                        String IsRecipeNum1 = "FoodState" + args["FoodNum1"].Tostring();
                        String IsRecipeNum2 = "FoodState" + args["FoodNum2"].Tostring();

                        var recipe0 = getUserInfoData.InfoResultPayload.UserReadOnlyData.GetValueOrDefault(IsRecipeNum0)?.Value;
                        var recipe1 = getUserInfoData.InfoResultPayload.UserReadOnlyData.GetValueOrDefault(IsRecipeNum1)?.Value;
                        var recipe2 = getUserInfoData.InfoResultPayload.UserReadOnlyData.GetValueOrDefault(IsRecipeNum2)?.Value;

                        List<FoodStateData> foodStateData = new List<FoodStateData>();
                        if (recipe0 != null) foodStateData.Add(PlayFabSimpleJson.DeserializeObject<FoodStateData>(recipe0));
                        if (recipe1 != null) foodStateData.Add(PlayFabSimpleJson.DeserializeObject<FoodStateData>(recipe1));
                        if (recipe2 != null) foodStateData.Add(PlayFabSimpleJson.DeserializeObject<FoodStateData>(recipe2));

                        //레시피 검증 시작
                        foreach (var recipe in requirementrecipe)
                        {
                            bool isfoodavailable = false;
                            foreach (var foodstate in foodStateData)
                            {
                                if (foodstate.FoodName == recipe)
                                {
                                    isfoodavailable = true;
                                }
                            }
                            if (!isfoodavailable) return new BadRequestObjectResult("No Food In Here!");
                        }

                        //데이터 초기화
                        var updatefoodStateData = new FoodStateDataValue("none", false, -1, -1, 0);
                        if (recipe0 != null) await UpdateUserReadOnlyDataAsync(serverApi, playFabId, IsRecipeNum0, updatefoodStateData);
                        if (recipe1 != null) await UpdateUserReadOnlyDataAsync(serverApi, playFabId, IsRecipeNum1, updatefoodStateData);
                        if (recipe2 != null) await UpdateUserReadOnlyDataAsync(serverApi, playFabId, IsRecipeNum2, updatefoodStateData);
                        await UpdateUserReadOnlyDataAsync(serverApi, playFabId, "StoryRecipe", CurrentAchievement.name);
                        break;

                    case "tutorial":
                        var usertutorialdata = getUserInfoData.InfoResultPayload.UserReadOnlyData.GetValueOrDefault("Tutorial")?.Value;
                        Dictionary<string, bool> Tutorialdata = PlayFabSimpleJson.DeserializeObject<Dictionary<string, bool>>(usertutorialdata);

                        foreach (var targettutorial in requirement.target)
                        {
                            if (!Tutorialdata.ContainsKey(targettutorial)) return new BadRequestObjectResult("condition is not met.");
                        }
                        break;

                    case "storeReputation":
                        if (requirement.amount > getUserInfoData.InfoResultPayload.PlayerStatistics.FirstOrDefault(item => item.StatisticName == "STOREREPUTATION").Value)
                            return new BadRequestObjectResult("condition is not met.");
                        break;

                    default:
                        return new BadRequestObjectResult("no type");
                }

                //보상 증정
                switch (CurrentAchievement.reward.type)
                {
                    case "story":
                        break;
                    case "recipe":
                        var recipeStateJson = getUserInfoData.InfoResultPayload.UserReadOnlyData.GetValueOrDefault("Recipes")?.Value;
                        var recipeStateData = PlayFabSimpleJson.DeserializeObject<List<string>>(recipeStateJson);

                        foreach (var recipe in CurrentAchievement.reward.target)
                        {
                            recipeStateData.Add(recipe);
                        }
                        await UpdateUserReadOnlyDataAsync(serverApi, playFabId, "Recipes", recipeStateData);
                        break;
                    case "cash":
                        // ADD VIRTUAL CURRENCY
                        await serverApi.AddUserVirtualCurrencyAsync(new AddUserVirtualCurrencyRequest
                        {
                            Amount = CurrentAchievement.reward.amount,
                            PlayFabId = playFabId,
                            VirtualCurrency = "CH"
                        });
                        break;
                    case "gold":
                        // ADD VIRTUAL CURRENCY
                        await serverApi.AddUserVirtualCurrencyAsync(new AddUserVirtualCurrencyRequest
                        {
                            Amount = CurrentAchievement.reward.amount,
                            PlayFabId = playFabId,
                            VirtualCurrency = "GD"
                        });
                        break;

                    default:
                        return new BadRequestObjectResult("no type");
                }

                //플레이어 통계 최신화
                var request = new UpdatePlayerStatisticsRequest
                {
                    PlayFabId = playFabId,
                    Statistics = new List<StatisticUpdate>()
                };

                int CurrentStatistics = getUserInfoData.InfoResultPayload.PlayerStatistics.FirstOrDefault(item => item.StatisticName == aTType.ToString())?.Value ?? 0;

                request.Statistics.Add(new StatisticUpdate
                {
                    StatisticName = aTType.ToString(),
                    Value = CurrentStatistics + 1
                });

                await serverApi.UpdatePlayerStatisticsAsync(request);

            }
            catch (Exception ex)
            {
                return new BadRequestObjectResult($"Something Went Wrong! {ex.Message}");
            }

            return new OkObjectResult(new());
        }
    }
}
