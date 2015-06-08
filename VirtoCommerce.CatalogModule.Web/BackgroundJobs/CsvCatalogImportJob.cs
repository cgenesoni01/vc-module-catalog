﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Web;
using coreModel = VirtoCommerce.Domain.Catalog.Model;
using VirtoCommerce.Domain.Catalog.Services;
using VirtoCommerce.Platform.Core.Asset;
using VirtoCommerce.Platform.Core.Caching;
using VirtoCommerce.Platform.Core.Notification;
using VirtoCommerce.Platform.Core.Common;
using System.IO;
using CsvHelper;
using System.Text;
using System.Reflection;
using VirtoCommerce.CatalogModule.Web.Model.Notifications;
using webModel = VirtoCommerce.CatalogModule.Web.Model;
using CsvHelper.Configuration;
using VirtoCommerce.Domain.Pricing.Model;
using System.Globalization;
using VirtoCommerce.Domain.Inventory.Model;
using VirtoCommerce.Domain.Pricing.Services;
using VirtoCommerce.Domain.Inventory.Services;
using VirtoCommerce.Domain.Commerce.Services;
using System.Threading.Tasks;


namespace VirtoCommerce.CatalogModule.Web.BackgroundJobs
{
	public class CsvCatalogImportJob
	{
		private readonly char[] _categoryDelimiters = new char[] { '/', '|', '\\', '>' };
		private readonly ICatalogService _catalogService;
		private readonly ICategoryService _categoryService;
		private readonly IItemService _productService;
		private readonly INotifier _notifier;
		private readonly CacheManager _cacheManager;
		private readonly IBlobStorageProvider _blobStorageProvider;
		private readonly ISkuGenerator _skuGenerator;
		private readonly IPricingService _pricingService;
		private readonly IInventoryService _inventoryService;
		private readonly ICommerceService _commerceService;
		private object _lockObject = new object();

		public CsvCatalogImportJob(ICatalogService catalogService, ICategoryService categoryService, IItemService productService,
								INotifier notifier, CacheManager cacheManager, IBlobStorageProvider blobProvider, ISkuGenerator skuGenerator,
								IPricingService pricingService, IInventoryService inventoryService, ICommerceService commerceService)
		{
			_catalogService = catalogService;
			_categoryService = categoryService;
			_productService = productService;
			_notifier = notifier;
			_cacheManager = cacheManager;
			_blobStorageProvider = blobProvider;
			_skuGenerator = skuGenerator;
			_pricingService = pricingService;
			_inventoryService = inventoryService;
			_commerceService = commerceService;
		}

		public virtual void DoImport(webModel.CsvImportConfiguration configuration, ImportNotification notification)
		{
			var csvProducts = new List<coreModel.CatalogProduct>();
			var catalog = _catalogService.GetById(configuration.CatalogId);
			try
			{
				using (var reader = new CsvReader(new StreamReader(_blobStorageProvider.OpenReadOnly(configuration.FileUrl))))
				{

					reader.Configuration.Delimiter = configuration.Delimiter;
					var initialized = false;
					while (reader.Read())
					{
						if (!initialized)
						{
							//Notification
							notification.Description = "Configuration...";
							_notifier.Upsert(notification);

							//set import configuration based on mapping info
							var productMap = new ProductMap(reader.FieldHeaders, configuration, catalog);
							reader.Configuration.RegisterClassMap(productMap);
							initialized = true;

							//Notification
							notification.Description = "Reading products from csv...";
							_notifier.Upsert(notification);
						}

						try
						{
							var csvProduct = reader.GetRecord<coreModel.CatalogProduct>();
							csvProducts.Add(csvProduct);
						}
						catch(Exception ex)
						{
							notification.ErrorCount++;
							notification.Errors.Add(ex.ToString());
						}
					}
				}

				SaveCategoryTree(catalog, csvProducts, notification);
				SaveProducts(catalog, csvProducts, notification);
			}
			catch (Exception ex)
			{
				notification.Description = "Export error";
				notification.ErrorCount++;
				notification.Errors.Add(ex.ToString());
			}
			finally
			{
				notification.Finished = DateTime.UtcNow;
				notification.Description = "Import finished" + (notification.Errors.Any() ? " with errors" : " successfully");
				_notifier.Upsert(notification);
			}


		}

	

		private void SaveCategoryTree(coreModel.Catalog catalog, IEnumerable<coreModel.CatalogProduct> csvProducts, ImportNotification notification)
		{
			var categories = new List<coreModel.Category>();
			csvProducts = csvProducts.Where(x => x.CategoryId == null && x.Category != null);
			notification.ProcessedCount = 0;
			foreach (var csvProduct in csvProducts)
			{
				var productCategoryNames = csvProduct.Category.Path.Split('/', '|', '\\', '>');
				ICollection<coreModel.Category> currentLevelCategories = categories;
				string parentCategoryId = null;
				foreach (var categoryName in productCategoryNames)
				{
					var category = currentLevelCategories.FirstOrDefault(x => x.Name == categoryName);
					if (category == null)
					{
						category = _categoryService.Create(new coreModel.Category() { Name = categoryName, Code = categoryName.GenerateSlug(), CatalogId = catalog.Id, ParentId = parentCategoryId });
						category.Children = new List<coreModel.Category>();
						currentLevelCategories.Add(category);

						//Raise notification each notifyCategorySizeLimit category
						notification.Description = string.Format("Creating categories: {0} created", ++notification.ProcessedCount);
						_notifier.Upsert(notification);
					}
					csvProduct.Category = category;
					currentLevelCategories = category.Children;
					parentCategoryId = category.Id;
				}
			}

		}


		private void SaveProducts(coreModel.Catalog catalog, IEnumerable<coreModel.CatalogProduct> csvProducts, ImportNotification notification)
		{
			int counter = 0;
			var notifyProductSizeLimit = 10;
			notification.TotalCount = csvProducts.Count();

			var defaultFulfilmentCenter = _commerceService.GetAllFulfillmentCenters().FirstOrDefault();
			var options = new ParallelOptions() 
			{
				 MaxDegreeOfParallelism = 10
			};

			Parallel.ForEach(csvProducts.OrderBy(x => x.MainProductId ?? ""), options, csvProduct =>
			{
				var isNewProduct = csvProduct.IsTransient();
				//Try to set parent relations
				var parentProduct = csvProducts.FirstOrDefault(x => ((csvProduct.MainProductId != null && (x.Id == csvProduct.MainProductId || x.Code == csvProduct.MainProductId)) || (x.Name == csvProduct.Name)) && !x.IsTransient());
				csvProduct.MainProductId = parentProduct != null ? parentProduct.Id : null;

				csvProduct.CatalogId = catalog.Id;

				try
				{
					if (String.IsNullOrEmpty(csvProduct.Code))
					{
						csvProduct.Code = _skuGenerator.GenerateSku(csvProduct);
					}

					if (!isNewProduct)
					{
						_productService.Update(new coreModel.CatalogProduct[] { csvProduct });
					}
					else
					{
						var newProduct = _productService.Create(csvProduct);
						csvProduct.Id = newProduct.Id;
					}

					//Create price in default price list
					if (csvProduct.Prices != null && csvProduct.Prices.Any())
					{
						var price = csvProduct.Prices.First();
						price.ProductId = csvProduct.Id;
						if (isNewProduct)
						{
							_pricingService.CreatePrice(price);
						}
						else
						{
							_pricingService.UpdatePrices(new Price[] {  price });
						}
					}

					//Create inventory
					if (csvProduct.Inventories != null && csvProduct.Inventories.Any())
					{
						var inventory = csvProduct.Inventories.First();
						inventory.ProductId = csvProduct.Id;
						inventory.FulfillmentCenterId = defaultFulfilmentCenter.Id;
						_inventoryService.UpsertInventory(csvProduct.Inventories.First());
					}
				}
				catch (Exception ex)
				{
					lock (_lockObject)
					{
						notification.ErrorCount++;
						notification.Errors.Add(ex.ToString());
						_notifier.Upsert(notification);
					}
				}
				finally
				{
					lock (_lockObject)
					{
						//Raise notification each notifyProductSizeLimit category
						counter++;
						notification.ProcessedCount = counter;
						notification.Description = string.Format("Creating products: {0} of {1} created", notification.ProcessedCount, notification.TotalCount);
						if (counter % notifyProductSizeLimit == 0)
						{
							_notifier.Upsert(notification);
						}
					}
				}
			});

		}



		public sealed class ProductMap : CsvClassMap<coreModel.CatalogProduct>
		{
			public ProductMap(string[] allColumns, webModel.CsvImportConfiguration importConfiguration, coreModel.Catalog catalog)
			{

				var defaultLanguge = catalog.DefaultLanguage != null ? catalog.DefaultLanguage.LanguageCode : "EN-US";
				//Dynamical map scalar product fields use by manual mapping information
				foreach (var mappingConfigurationItem in importConfiguration.MappingItems.Where(x => x.CsvColumnName != null || x.CustomValue != null))
				{
					var propertyInfo = typeof(coreModel.CatalogProduct).GetProperty(mappingConfigurationItem.EntityColumnName);
					if (propertyInfo != null)
					{
						var newMap = new CsvPropertyMap(propertyInfo);
						//Map fields if mapping specified
						if (mappingConfigurationItem.CsvColumnName != null)
						{
							newMap.Name(mappingConfigurationItem.CsvColumnName);
						}
						//And default values if it specified
						if (mappingConfigurationItem.CustomValue != null)
						{
							newMap.Default(mappingConfigurationItem.CustomValue);
						}
						PropertyMaps.Add(newMap);
					}
				}

				//Map Sku -> Code
				Map(x => x.Code).ConvertUsing(x =>
				{
					return GetCsvField("Sku", x, importConfiguration);
				});

				//Map ParentSku -> main product
				Map(x => x.MainProductId).ConvertUsing(x =>
				{
					var parentSku =  GetCsvField("ParentSku", x, importConfiguration);
					return !String.IsNullOrEmpty(parentSku) ? parentSku : null;
				});

				//Map assets (images)
				Map(x => x.Assets).ConvertUsing(x =>
					{
						var retVal = new List<coreModel.ItemAsset>();
						var primaryImageUrl = GetCsvField("PrimaryImage", x, importConfiguration);
						var altImageUrl = GetCsvField("AltImage", x, importConfiguration);
						if (!String.IsNullOrEmpty(primaryImageUrl))
						{
							retVal.Add(new coreModel.ItemAsset
							{
								Type = coreModel.ItemAssetType.Image,
								Group = "primaryimage",
								Url = primaryImageUrl
							});
						}

						if (!String.IsNullOrEmpty(altImageUrl))
						{
							retVal.Add(new coreModel.ItemAsset
							{
								Type = coreModel.ItemAssetType.Image,
								Group = "image",
								Url = altImageUrl
							});
						}

						return retVal;
					});

				//Map category
				Map(x => x.Category).ConvertUsing(x => new coreModel.Category { Path = GetCsvField("Category", x, importConfiguration) });

				//Map Reviews
				Map(x => x.Reviews).ConvertUsing(x =>
				{
					var reviews = new List<coreModel.EditorialReview>();
					var content = GetCsvField("Review", x, importConfiguration);
					if (!String.IsNullOrEmpty(content))
					{
						reviews.Add(new coreModel.EditorialReview { Content = content, LanguageCode = defaultLanguge });
					}
					return reviews;

				});

				//Map Seo
				Map(x => x.SeoInfos).ConvertUsing(x =>
				{
					var seoInfos = new List<coreModel.SeoInfo>();
					var seoUrl = GetCsvField("SeoUrl", x, importConfiguration);
					var seoDescription = GetCsvField("SeoDescription", x, importConfiguration);
					var seoTitle = GetCsvField("SeoTitle", x, importConfiguration);
					if (!String.IsNullOrEmpty(seoUrl) || !String.IsNullOrEmpty(seoTitle) || !String.IsNullOrEmpty(seoDescription))
					{
						seoUrl = new string[] { seoUrl, seoDescription, seoTitle }.Where(y => !String.IsNullOrEmpty(y)).FirstOrDefault();
						seoUrl = seoUrl.Substring(0, Math.Min(seoUrl.Length, 240));
						seoInfos.Add(new coreModel.SeoInfo { LanguageCode = defaultLanguge, SemanticUrl = seoUrl.GenerateSlug(), MetaDescription = seoDescription, PageTitle = seoTitle});
					}
					return seoInfos; 
				});

				//Map Prices
				Map(x => x.Prices).ConvertUsing(x =>
				{
					var prices = new List<Price>();
					var priceId = GetCsvField("PriceId", x, importConfiguration);
					var listPrice = GetCsvField("Price", x, importConfiguration);
					var salePrice = GetCsvField("SalePrice", x, importConfiguration);
					var currency = GetCsvField("Currency", x, importConfiguration);

					if (!String.IsNullOrEmpty(listPrice) && !String.IsNullOrEmpty(currency))
					{
						prices.Add(new Price
						{
							Id = priceId,
							List = Convert.ToDecimal(listPrice, CultureInfo.InvariantCulture),
							Sale = salePrice != null ? (decimal?)Convert.ToDecimal(listPrice, CultureInfo.InvariantCulture) : null,
							Currency = EnumUtility.SafeParse<CurrencyCodes>(currency, CurrencyCodes.USD)
						});
					}
					return prices;
				});

				//Map inventories
				Map(x => x.Inventories).ConvertUsing(x =>
				{
					var inventories = new List<InventoryInfo>();
					var quantity = GetCsvField("Quantity", x, importConfiguration);
					var allowBackorder = GetCsvField("AllowBackorder", x, importConfiguration);

					if (!String.IsNullOrEmpty(quantity))
					{
						inventories.Add(new InventoryInfo
						{
							AllowBackorder = allowBackorder.TryParse(false),
							InStockQuantity = (long)quantity.TryParse(0.0m)
						});
					}
					return inventories;
				});

				//Map properties
				if (importConfiguration.PropertyCsvColumns != null && importConfiguration.PropertyCsvColumns.Any())
				{
					Map(x => x.PropertyValues).ConvertUsing(x => importConfiguration.PropertyCsvColumns.Select(column => new coreModel.PropertyValue { PropertyName = column, Value = x.GetField<string>(column) }).ToList());
				}
			}

			private static string GetCsvField(string name, ICsvReaderRow row, webModel.CsvImportConfiguration configuration)
			{
				var mapping = configuration.MappingItems.First(y => y.EntityColumnName == name);
				if (mapping == null)
				{
					throw new NullReferenceException("mapping");
				}

				var retVal = mapping.CustomValue;
				if (mapping.CsvColumnName != null)
				{
					retVal = row.GetField<string>(mapping.CsvColumnName);
				}
				return retVal;
			}
		}



	}
}