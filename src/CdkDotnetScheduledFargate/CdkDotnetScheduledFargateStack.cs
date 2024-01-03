using Amazon.CDK;
using Constructs;
using Amazon.CDK.AWS.EC2;
using Amazon.CDK.AWS.ECS;
using Amazon.CDK.AWS.IAM;
using Amazon.CDK.AWS.Logs;
using Amazon.CDK.AWS.ECS.Patterns;
using Amazon.CDK.AWS.Events;
using Amazon.CDK.AWS.Events.Targets;
using Amazon.CDK.AWS.SNS;
using Amazon.CDK.AWS.SNS.Subscriptions;
using System.Collections.Generic; 
using System.Text.Json;
using System.IO;
using System;

namespace CdkDotnetScheduledFargate
{
    public class CdkDotnetScheduledFargateStack : Stack
    {

               // Class-level variable
        private readonly string serviceName = "ScheduledServiceTasks";

        internal CdkDotnetScheduledFargateStack(Construct scope, string id, IStackProps props = null) : base(scope, id, props)
        {

            var configText = File.ReadAllText("config.json");
            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true, // To handle case sensitivity
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            };

            var config = JsonSerializer.Deserialize<Config>(configText, options);

            if (config == null || string.IsNullOrEmpty(config.Email))
            {
                throw new Exception("Configuration is null or email is not provided.");
            }

          //  Console.WriteLine($"Email from config: {config.Email}"); 

            var vpc = new Vpc(this, "ContainerVpc", new VpcProps
            {
               // NatGateways = 1
                IpAddresses = IpAddresses.Cidr("172.31.0.0/16"),
                NatGateways = 1,
                MaxAzs = 3,
                SubnetConfiguration = new ISubnetConfiguration[]
                {
                    new SubnetConfiguration
                    {
                        CidrMask = 20,
                        Name = "public",
                        SubnetType = SubnetType.PUBLIC
                    },
                    new SubnetConfiguration
                    {
                        CidrMask = 20,
                        Name = "application",
                        SubnetType = SubnetType.PRIVATE_WITH_EGRESS
                    },
                    new SubnetConfiguration
                    {
                        CidrMask = 20,
                        Name = "data",
                        SubnetType = SubnetType.PRIVATE_ISOLATED
                    }
                }
            });

              // Create an ECS Cluster
            var cluster = new Cluster(this, "scheduled-task-cluster", new ClusterProps
            {
                ClusterName = "scheduled-task-cluster",
                ContainerInsights = true,
                Vpc = vpc,

            });

            // Create a Fargate container image
            var image = ContainerImage.FromRegistry("amazonlinux:2");

            // Get Log Group
            var taskLogGroup = LogGroup.FromLogGroupName(this, "import-log-group", "/aws/ecs/scheduledTaskApp");

            // Create Log Group
            // var taskLogGroup = new LogGroup(this, "TaskLogGroup", new Amazon.CDK.AWS.Logs.LogGroupProps
            // {
            //     LogGroupName = "/aws/ecs/scheduledTaskApp",
            //     // Optionally, specify log retention. For example, RetentionDays.ONE_YEAR
            //     // Retention = RetentionDays.ONE_YEAR
            //     LogGroupClass = LogGroupClass.INFREQUENT_ACCESS
            // });
            // Create Execution Role
            var executionRole = new Role(this, $"{serviceName}-ecsAgentTaskExecutionRole", new RoleProps
            {
                RoleName = $"{serviceName}EcsAgentTaskExecutionRole",
                AssumedBy = new ServicePrincipal("ecs-tasks.amazonaws.com"),
                ManagedPolicies = new[]
                {
                    ManagedPolicy.FromAwsManagedPolicyName("CloudWatchFullAccess"),
                    ManagedPolicy.FromAwsManagedPolicyName("CloudWatchLogsFullAccess"),
                    ManagedPolicy.FromAwsManagedPolicyName("AmazonEC2ContainerRegistryReadOnly"),
                    ManagedPolicy.FromAwsManagedPolicyName("service-role/AmazonECSTaskExecutionRolePolicy")
                }
            });

            // Create Task Role
            var taskRole = new Role(this, "ecsContainerRole", new RoleProps
            {
                RoleName = $"{serviceName}-ECSContainerTaskRole",
                AssumedBy = new ServicePrincipal("ecs-tasks.amazonaws.com"),
                ManagedPolicies = new[]
                {
                    ManagedPolicy.FromAwsManagedPolicyName("CloudWatchFullAccess"),
                    ManagedPolicy.FromAwsManagedPolicyName("AWSXRayDaemonWriteAccess"),
                    ManagedPolicy.FromAwsManagedPolicyName("service-role/AmazonECSTaskExecutionRolePolicy"),
                    ManagedPolicy.FromAwsManagedPolicyName("AmazonEC2ContainerRegistryPowerUser"),
                }
            });

            var fargateTaskDefinition  = new FargateTaskDefinition(this, "fargateTaskDef", new FargateTaskDefinitionProps{
                ExecutionRole = executionRole,
                TaskRole = taskRole,
                Family = $"{serviceName}"
               // Cpu = "256",
               // MemoryMiB = "512",
            }); 

            fargateTaskDefinition.AddContainer("Container", new ContainerDefinitionOptions
            {
                Image = image,
                Logging = LogDriver.AwsLogs(new AwsLogDriverProps
                {
                    StreamPrefix = id,
                    LogGroup = taskLogGroup
                }),
               // Command = new[] { "sh", "-c", "sleep 5" },
                Command = new[] 
                { 
                    "sh", "-c", 
                    "echo 'starting task execution'  && echo \"StackId is: $StackId\" && sleep 180 && echo 'completed task execution'" 
                },
                Environment = new Dictionary<string, string>
                {
                    { "StackId", id }
                }
                // MemoryLimitMiB and Cpu can be specified here if needed
            });

            new ScheduledFargateTask(this, "AmazonLinuxSleepTask", new ScheduledFargateTaskProps
            {
                Schedule = Amazon.CDK.AWS.ApplicationAutoScaling.Schedule.Cron(new Amazon.CDK.AWS.ApplicationAutoScaling.CronOptions
                {
                    //Specify in UTC Time: 
                    //See converter: https://dateful.com/convert/utc 
                    Minute = "00",
                    Hour = "15",
                    Day = "*",
                    Month = "*"
                }),
                Cluster = cluster,
                PlatformVersion = FargatePlatformVersion.LATEST,
                ScheduledFargateTaskDefinitionOptions = new ScheduledFargateTaskDefinitionOptions{
                    TaskDefinition = fargateTaskDefinition,
                }

            });

/*      
             // Create SNS Topic
            var taskStateAlert = new Topic(this, "TaskStateAlert", new TopicProps
            {
                TopicName = "TaskStateAlert"
            });

            // Email Subscription
            taskStateAlert.AddSubscription(new EmailSubscription(config.Email));

           // Rule for Task Stopped
            new Rule(this, "TaskStoppedRule", new RuleProps
            {
                EventPattern = new EventPattern
                {
                    Source = new[] { "aws.ecs" },
                    DetailType = new[] { "ECS Task State Change" },
                    Detail = new Dictionary<string, object>
                    {
                        { "lastStatus", new[] { "STOPPED" } },
                        { "stoppedReason", new[] { "Essential container in task exited" } }
                    }
                },
                Targets = new IRuleTarget[]
                {
                    new SnsTopic(taskStateAlert)
                }
            });

            // Rule for Task Started
            new Rule(this, "TaskStartedRule", new RuleProps
            {
                EventPattern = new EventPattern
                {
                    Source = new[] { "aws.ecs" },
                    DetailType = new[] { "ECS Task State Change" },
                    Detail = new Dictionary<string, object>
                    {
                        { "lastStatus", new[] { "STARTED" } }
                    }
                },
                Targets = new IRuleTarget[]
                {
                    new SnsTopic(taskStateAlert)
                }
            });

         */
            Amazon.CDK.Tags.Of(this).Add("environment", config.Environment);
            Amazon.CDK.Tags.Of(this).Add("costcenter", config.CostCenter);
        }

         private class Config
        {
            public string Email { get; set; }
            public string Environment { get; set; }
            public string CostCenter { get; set; }
        }
        
    }
    
}
