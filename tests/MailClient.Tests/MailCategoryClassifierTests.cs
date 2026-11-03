using MailClient.Core;
using MailClient.Core.Classification;

namespace MailClient.Tests;

public class MailCategoryClassifierTests
{
    [Theory]
    [InlineData("noreply@alipay.com", "支付宝", "您的账单已出：10 月消费 ¥1,280.00", "", MailCategory.Transactions)]
    [InlineData("order@jd.com", "京东", "订单已发货，快递单号 SF123456", "", MailCategory.Transactions)]
    [InlineData("zhang.wei@company.com", "张伟", "项目进度更新与下周计划", "请查看附件中的排期", MailCategory.Primary)]
    [InlineData("news@shop.com", "某商城", "双11 限时特价 全场 5 折，优惠券请领取", "", MailCategory.Promotions)]
    [InlineData("noreply@github.com", "GitHub", "[mailclient] Pull request merged", "", MailCategory.Updates)]
    public void Classify_Demo_Cases(string address, string name, string subject, string preview, MailCategory expected)
        => Assert.Equal(expected, MailCategoryClassifier.Classify(address, name, subject, preview));

    [Fact]
    public void Classify_Defaults_To_Primary_For_Plain_Person_Mail()
        => Assert.Equal(MailCategory.Primary, MailCategoryClassifier.Classify("li.si@example.com", "李四", "周末聚餐吗", "我订了位置"));

    [Fact]
    public void Classify_Handles_Nulls()
        => Assert.Equal(MailCategory.Primary, MailCategoryClassifier.Classify("a@b.com", null, "", null));
}
