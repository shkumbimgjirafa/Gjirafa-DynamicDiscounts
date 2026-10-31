-- Generated from live-discount-schema.json and live-price-schema.json by SandboxSchema.CreateStatements. Do not edit.
IF OBJECT_ID('[shop_sandbox_GjirafaEcommerce].[TierPrice]') IS NOT NULL DROP TABLE [shop_sandbox_GjirafaEcommerce].[TierPrice];
GO
IF OBJECT_ID('[shop_sandbox_GjirafaEcommerce].[Product]') IS NOT NULL DROP TABLE [shop_sandbox_GjirafaEcommerce].[Product];
GO
IF OBJECT_ID('[shop_sandbox_GjirafaEcommerce].[Discount_AppliedToProducts]') IS NOT NULL DROP TABLE [shop_sandbox_GjirafaEcommerce].[Discount_AppliedToProducts];
GO
IF OBJECT_ID('[shop_sandbox_GjirafaEcommerce].[Discount]') IS NOT NULL DROP TABLE [shop_sandbox_GjirafaEcommerce].[Discount];
GO
IF SCHEMA_ID('shop_sandbox_GjirafaEcommerce') IS NULL EXEC('CREATE SCHEMA [shop_sandbox_GjirafaEcommerce]');
GO
CREATE TABLE [shop_sandbox_GjirafaEcommerce].[Discount] (
    [Id] int IDENTITY(1,1) NOT NULL,
    [Name] nvarchar(400) NOT NULL,
    [CouponCode] nvarchar(200) NULL,
    [AdminComment] nvarchar(max) NULL,
    [DiscountTypeId] int NOT NULL,
    [UsePercentage] bit NOT NULL,
    [DiscountPercentage] decimal(18,4) NOT NULL,
    [DiscountAmount] decimal(18,4) NOT NULL,
    [MaximumDiscountAmount] decimal(18,4) NULL,
    [StartDateUtc] datetime2(7) NULL,
    [EndDateUtc] datetime2(7) NULL,
    [RequiresCouponCode] bit NOT NULL,
    [IsCumulative] bit NOT NULL,
    [DiscountLimitationId] int NOT NULL,
    [LimitationTimes] int NOT NULL,
    [MaximumDiscountedQuantity] int NULL,
    [AppliedToSubCategories] bit NOT NULL,
    [StoreId] int NOT NULL,
    [DiscountApplyTypeId] smallint NOT NULL,
    [DiscountPlatformTypeId] smallint NOT NULL,
    [IsVendorDiscount] bit NOT NULL,
    CONSTRAINT [PK_Discount] PRIMARY KEY CLUSTERED ([Id])
);
GO
CREATE TABLE [shop_sandbox_GjirafaEcommerce].[Discount_AppliedToProducts] (
    [Discount_Id] int NOT NULL,
    [Product_Id] int NOT NULL,
    CONSTRAINT [PK_Discount_AppliedToProducts] PRIMARY KEY CLUSTERED ([Discount_Id], [Product_Id])
);
GO
CREATE NONCLUSTERED INDEX [IX_Discount_AppliedToProducts_Discount_Id] ON [shop_sandbox_GjirafaEcommerce].[Discount_AppliedToProducts] ([Discount_Id]);
GO
CREATE NONCLUSTERED INDEX [IX_Discount_AppliedToProducts_Product_Id] ON [shop_sandbox_GjirafaEcommerce].[Discount_AppliedToProducts] ([Product_Id]);
GO
CREATE TABLE [shop_sandbox_GjirafaEcommerce].[Product] (
    [Id] int IDENTITY(1,1) NOT NULL,
    [Sku] nvarchar(800) NULL,
    [HasTierPrices] bit NOT NULL,
    [HasDiscountsApplied] bit NOT NULL,
    [Deleted] bit NOT NULL,
    [HasDiscountsAppliedStoreIds] nvarchar(40) NULL,
    CONSTRAINT [PK_Product] PRIMARY KEY CLUSTERED ([Id])
);
GO
CREATE NONCLUSTERED INDEX [IX_Sandbox_Product_Sku] ON [shop_sandbox_GjirafaEcommerce].[Product] ([Sku]);
GO
CREATE TABLE [shop_sandbox_GjirafaEcommerce].[TierPrice] (
    [Id] int IDENTITY(1,1) NOT NULL,
    [CustomerRoleId] int NULL,
    [ProductId] int NOT NULL,
    [StoreId] int NOT NULL,
    [Quantity] int NOT NULL,
    [Price] decimal(18,2) NOT NULL,
    [StartDateTimeUtc] datetime2(7) NULL,
    [EndDateTimeUtc] datetime2(7) NULL,
    [OldPrice] decimal(18,2) NOT NULL,
    [ProfitMargin] decimal(6,4) NULL,
    CONSTRAINT [PK_TierPrice] PRIMARY KEY CLUSTERED ([Id])
);
GO
CREATE NONCLUSTERED INDEX [IX_TierPrice_CustomerRoleId] ON [shop_sandbox_GjirafaEcommerce].[TierPrice] ([CustomerRoleId]);
GO
CREATE NONCLUSTERED INDEX [IX_TierPrice_ProductId] ON [shop_sandbox_GjirafaEcommerce].[TierPrice] ([ProductId]);
GO
IF OBJECT_ID('[shop_sandbox_GjirafaMall].[TierPrice]') IS NOT NULL DROP TABLE [shop_sandbox_GjirafaMall].[TierPrice];
GO
IF OBJECT_ID('[shop_sandbox_GjirafaMall].[Product]') IS NOT NULL DROP TABLE [shop_sandbox_GjirafaMall].[Product];
GO
IF OBJECT_ID('[shop_sandbox_GjirafaMall].[Discount_AppliedToProducts]') IS NOT NULL DROP TABLE [shop_sandbox_GjirafaMall].[Discount_AppliedToProducts];
GO
IF OBJECT_ID('[shop_sandbox_GjirafaMall].[Discount]') IS NOT NULL DROP TABLE [shop_sandbox_GjirafaMall].[Discount];
GO
IF SCHEMA_ID('shop_sandbox_GjirafaMall') IS NULL EXEC('CREATE SCHEMA [shop_sandbox_GjirafaMall]');
GO
CREATE TABLE [shop_sandbox_GjirafaMall].[Discount] (
    [Id] int IDENTITY(1,1) NOT NULL,
    [Name] nvarchar(400) NOT NULL,
    [CouponCode] nvarchar(200) NULL,
    [AdminComment] nvarchar(max) NULL,
    [DiscountTypeId] int NOT NULL,
    [UsePercentage] bit NOT NULL,
    [DiscountPercentage] decimal(18,4) NOT NULL,
    [DiscountAmount] decimal(18,4) NOT NULL,
    [MaximumDiscountAmount] decimal(18,4) NULL,
    [StartDateUtc] datetime2(7) NULL,
    [EndDateUtc] datetime2(7) NULL,
    [RequiresCouponCode] bit NOT NULL,
    [IsCumulative] bit NOT NULL,
    [DiscountLimitationId] int NOT NULL,
    [LimitationTimes] int NOT NULL,
    [MaximumDiscountedQuantity] int NULL,
    [AppliedToSubCategories] bit NOT NULL,
    [StoreId] int NOT NULL,
    [DiscountApplyTypeId] smallint NOT NULL,
    [DiscountPlatformTypeId] smallint NOT NULL,
    [DisplayAvailabilityTime] bit NOT NULL,
    [IsVendorDiscount] bit NOT NULL,
    CONSTRAINT [PK_Discount] PRIMARY KEY CLUSTERED ([Id])
);
GO
CREATE NONCLUSTERED INDEX [IX_Discount_Active_Store_Coupon] ON [shop_sandbox_GjirafaMall].[Discount] ([StoreId], [EndDateUtc], [DiscountPlatformTypeId], [StartDateUtc]) INCLUDE ([AdminComment], [AppliedToSubCategories], [CouponCode], [DiscountAmount], [DiscountApplyTypeId], [DiscountLimitationId], [DiscountPercentage], [DiscountTypeId], [DisplayAvailabilityTime], [Id], [IsCumulative], [LimitationTimes], [MaximumDiscountAmount], [MaximumDiscountedQuantity], [Name], [RequiresCouponCode], [UsePercentage]);
GO
CREATE NONCLUSTERED INDEX [IX_Discount_CouponCode_Dates_Active] ON [shop_sandbox_GjirafaMall].[Discount] ([CouponCode], [StartDateUtc], [EndDateUtc]) INCLUDE ([Id], [Name], [AdminComment], [DiscountTypeId], [UsePercentage], [DiscountPercentage], [DiscountAmount], [MaximumDiscountAmount], [RequiresCouponCode], [IsCumulative], [DiscountLimitationId], [LimitationTimes], [MaximumDiscountedQuantity], [AppliedToSubCategories], [StoreId], [DiscountApplyTypeId], [DiscountPlatformTypeId], [DisplayAvailabilityTime]);
GO
CREATE TABLE [shop_sandbox_GjirafaMall].[Discount_AppliedToProducts] (
    [Discount_Id] int NOT NULL,
    [Product_Id] int NOT NULL,
    CONSTRAINT [PK_Discount_AppliedToProducts] PRIMARY KEY CLUSTERED ([Discount_Id], [Product_Id])
);
GO
CREATE NONCLUSTERED INDEX [IX_Discount_AppliedToProducts_ProductId] ON [shop_sandbox_GjirafaMall].[Discount_AppliedToProducts] ([Product_Id], [Discount_Id]);
GO
CREATE TABLE [shop_sandbox_GjirafaMall].[Product] (
    [Id] int IDENTITY(1,1) NOT NULL,
    [Sku] nvarchar(800) NULL,
    [HasTierPrices] bit NOT NULL,
    [HasDiscountsApplied] bit NOT NULL,
    [Deleted] bit NOT NULL,
    [HasDiscountsAppliedStoreIds] nvarchar(40) NULL,
    CONSTRAINT [PK_Product] PRIMARY KEY CLUSTERED ([Id])
);
GO
CREATE NONCLUSTERED INDEX [IX_Sandbox_Product_Sku] ON [shop_sandbox_GjirafaMall].[Product] ([Sku]);
GO
CREATE TABLE [shop_sandbox_GjirafaMall].[TierPrice] (
    [Id] int IDENTITY(1,1) NOT NULL,
    [CustomerRoleId] int NULL,
    [ProductId] int NOT NULL,
    [StoreId] int NOT NULL,
    [Quantity] int NOT NULL,
    [Price] decimal(18,4) NOT NULL,
    [StartDateTimeUtc] datetime2(7) NULL,
    [EndDateTimeUtc] datetime2(7) NULL,
    [OldPrice] decimal(18,4) NOT NULL,
    [ProfitMargin] decimal(6,4) NULL,
    CONSTRAINT [PK_TierPrice] PRIMARY KEY CLUSTERED ([Id])
);
GO
CREATE NONCLUSTERED INDEX [IX_TierPrice_ProductId] ON [shop_sandbox_GjirafaMall].[TierPrice] ([ProductId]);
GO
CREATE NONCLUSTERED INDEX [IX_TierPrice_StoreId_ProductId] ON [shop_sandbox_GjirafaMall].[TierPrice] ([StoreId], [ProductId]) INCLUDE ([Price], [OldPrice]);
GO
