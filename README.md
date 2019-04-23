### Containerized Activities in Durable Workflows

This sample application shows how we can combine Azure Container Instances with Azure Durable Functions to allow us to run steps in a durable workflow as containers. This allows us to implement long-running activities, use any custom Windows or Linux software, and mount Azure File Shares. 

The `deploy.ps1` script contains example code using the Azure CLI to create all necessary resources, build and deploy the sample app, initiate a custom workflow, and check on its status. One the demo scenarios shows using an FFMPEG container to generate a thumbnail of a video file.

I plan to write about this sample app on [my blog](https://markheath.net) soon.