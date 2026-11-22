namespace PCL.Core.App;

// 上游的密钥由源生成器在构建时从环境变量烘焙 本地裸构建拿不到 这里给个兜底
public static class RemixSecrets
{
    // 微软 OAuth 的 client id 换成自己注册的 Azure 应用就改这 也可用 PCL_MS_CLIENT_ID 覆盖
    public const string MSOAuthClientId = "c14b0370-8d75-42f8-b329-5b60d39e319f";

    // CurseForge 的 key 这是真密钥 对外发源码前记得删掉 或者换成自己申请的
    public const string CurseForgeApiKey = "$2a$10$TK3CR9laZ1oUb51B1rXZJ.UQ.bLHZGEZ8Y86nbMacMf1rorvdkBWG";
}
