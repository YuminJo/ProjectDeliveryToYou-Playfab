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
    public static class OnUpdateTutorial
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

        [FunctionName("OnUpdateTutorial")]
        public static async Task<dynamic> OnUpdateTutorials(
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

            if (args == null || args["TutorialName"] == null)
            {
                return new BadRequestObjectResult("Invalid request data.");
            }

            string TutorialName = args["TutorialName"].ToString();

            try
            {
                var tasks = new List<Task>();

                var getUserDataTask = serverApi.GetUserReadOnlyDataAsync(new PlayFab.ServerModels.GetUserDataRequest
                {
                    PlayFabId = playFabId,
                    Keys = new List<string>
                    {
                        "Tutorial"
                    }
                });
                tasks.Add(getUserDataTask);
                await Task.WhenAll(tasks);

                var getUserData = getUserDataTask.Result;
                //튜토리얼 여부 검증
                var TutorialStateJson = getUserData.Result.Data["Tutorial"]?.Value;
                var TutorialStateData = PlayFabSimpleJson.DeserializeObject<Dictionary<string, bool>>(TutorialStateJson);


                if (TutorialStateData != null)
                {
                    if (TutorialStateData.TryGetValue(TutorialName, out bool TutorialEnabled))
                    {
                        if (TutorialEnabled)
                        {
                            return new BadRequestObjectResult("The Tutorial Already Active");
                        }
                        else
                        {
                            TutorialStateData[TutorialName] = true;
                        }
                    }
                    else
                    {
                        TutorialStateData.Add(TutorialName, true);
                    }
                }

                await UpdateUserReadOnlyDataAsync(serverApi, playFabId, "Tutorial", TutorialStateData);

                return new OkObjectResult(new { success = true });
            }
            catch (Exception ex)
            {
                return new BadRequestObjectResult($"Something Went Wrong! {ex.Message}");
            }
        }
    }
}