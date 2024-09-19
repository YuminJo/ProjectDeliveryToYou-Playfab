using System;
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
using PlayFab.ServerModels;

namespace DeliveryToYou.Function
{
    public static class OnDeleteFood
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

        [FunctionName("OnDeleteFood")]
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

            if (args == null || args["FoodNumber"] == null)
            {
                return new BadRequestObjectResult("Invalid request data.");
            }

            string FoodNumber = args["FoodNumber"].ToString();
            string CurrentFood = "FoodState" + FoodNumber;

            var updatefoodStateData = new FoodStateDataValue("none", false, -1, -1, 0);
            await UpdateUserReadOnlyDataAsync(serverApi, playFabId, CurrentFood, updatefoodStateData);
            return new OkObjectResult("");
        }
    }
}