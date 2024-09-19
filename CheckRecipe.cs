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
using System.Linq;
using PlayFab.ServerModels;

namespace DeliveryToYou.Function
{
    public static class CheckRecipe
    {
        [FunctionName("ConsumeItem")]
        public static async Task<dynamic> ConsumeItem(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", "post", Route = null)] HttpRequest req,
        ILogger log)
        {
            string body = await req.ReadAsStringAsync();
            var context = JsonConvert.DeserializeObject<FunctionExecutionContext<dynamic>>(body);
            var args = context.FunctionArgument;

            // 引数の取り出し
            dynamic itemId = null;
            if (args != null && args["ItemId"] != null)
                itemId = args["ItemId"];

            // アイテム消費
            var result = await ConsumeItemAsync(context, itemId);

            // 結果の返却
            return new { resultValue = result };
        }

        private static async Task<List<string>> ConsumeItemAsync(FunctionExecutionContext<dynamic> context, dynamic itemInstances)
        {
            var apiSettings = new PlayFabApiSettings
            {
                TitleId = context.TitleAuthenticationContext.Id,
                DeveloperSecretKey = Environment.GetEnvironmentVariable("PLAYFAB_DEV_SECRET_KEY", EnvironmentVariableTarget.Process),
            };
            var serverApi = new PlayFabServerInstanceAPI(apiSettings);
            var itemIds = new List<string>();
            foreach (var itemId in itemInstances)
            {
                var result = await serverApi.ConsumeItemAsync(new PlayFab.ServerModels.ConsumeItemRequest()
                {
                    PlayFabId = context.CallerEntityProfile.Lineage.MasterPlayerAccountId,
                    ItemInstanceId = itemId,
                    ConsumeCount = 1
                });
                itemIds.Add(result.Result.ItemInstanceId);
            }
            return itemIds;
        }
    }
}
