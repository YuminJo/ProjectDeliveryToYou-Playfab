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
    public static class OnFarmBuy
    {
        private const string InvalidRequestMessage = "Invalid request data.";
        private const string FarmDataNotFoundMessage = "Farm data not found.";
        private const string NoMoneyMessage = "NO MONEY";
        private const string SomethingWentWrongMessage = "Something Went Wrong!";

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

        [FunctionName("OnFarmBuy")]
        public static async Task<dynamic> OnFarmBuys(
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
                var tasks = new List<Task>();

                var getUserInfoResponse = serverApi.GetPlayerCombinedInfoAsync(new GetPlayerCombinedInfoRequest
                {
                    PlayFabId = playFabId,
                    InfoRequestParameters = new GetPlayerCombinedInfoRequestParams()
                    {
                        GetUserVirtualCurrency = true,
                        GetUserReadOnlyData = true
                    }
                });

                await Task.WhenAll(tasks);

                var getUserInfoData = getUserInfoResponse.Result.Result;
                var getUserData = getUserInfoData.InfoResultPayload.UserReadOnlyData.GetValueOrDefault(currentFarm)?.Value;
                int getUserFarmCount = Int32.Parse(getUserInfoData.InfoResultPayload.UserReadOnlyData["FarmCount"].Value);

                FarmStateData farmStateData = PlayFabSimpleJson.DeserializeObject<FarmStateData>(getUserData);
                if (farmStateData == null || farmStateData.FarmActive)
                {
                    return new BadRequestObjectResult("Farm data not found.");
                }

                int currencyAmount = (getUserFarmCount < 6) ? getUserFarmCount * 3000 : getUserFarmCount * 150;
                string currencyType = (getUserFarmCount < 6) ? "GD" : "CH";


                if (getUserInfoData.InfoResultPayload.UserVirtualCurrency.TryGetValue(currencyType, out var userCurrencyAmount) && userCurrencyAmount >= currencyAmount)
                {
                    await serverApi.SubtractUserVirtualCurrencyAsync(new SubtractUserVirtualCurrencyRequest
                    {
                        Amount = currencyAmount,
                        PlayFabId = playFabId,
                        VirtualCurrency = currencyType
                    });
                }
                else
                {
                    return new BadRequestObjectResult(NoMoneyMessage);
                }

                var updatefarmStateData = new FarmStateDataValue(true, -1, "none", 0);
                await UpdateUserReadOnlyDataAsync(serverApi, playFabId, currentFarm, updatefarmStateData);
                await UpdateUserReadOnlyDataAsync(serverApi, playFabId, "FarmCount", getUserFarmCount + 1);

                return new OkObjectResult(new());
            }
            catch (Exception ex)
            {
                return new BadRequestObjectResult($"Something Went Wrong! {ex.Message}");
            }
        }
    }
}
