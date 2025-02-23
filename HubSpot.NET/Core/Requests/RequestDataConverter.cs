﻿using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Linq;
using System.Reflection;
using HubSpot.NET.Api;
using HubSpot.NET.Core.Attributes;
using HubSpot.NET.Core.Extensions;
using HubSpot.NET.Core.Interfaces;

namespace HubSpot.NET.Core.Requests
{
    public class RequestDataConverter
    {
        /// <summary>
        /// Converts the given <paramref name="entity"/> to a hubspot data entity.
        /// </summary>
        /// <param name="entity">The entity.</param>
        /// <param name="batchMode">If we're operating in batch mode the email must be specified outside of the props</param>
        /// <returns></returns>
        public dynamic ToHubspotDataEntity(IHubSpotModel entity, bool batchMode = false)
        {
            dynamic mapped = new ExpandoObject();

            mapped.Properties = new List<HubspotDataEntityProp>();

            var allProps = entity.GetType().GetProperties(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);

            foreach (var prop in allProps)
            {
                if (prop.HasIgnoreDataMemberAttribute()) { continue; }

                var propSerializedName = prop.GetPropSerializedName();
                if (prop.Name.Equals("RouteBasePath") || prop.Name.Equals("IsNameValue")) { continue; }

                // IF we have an complex type on the entity that we are trying to convert, let's NOT get the 
                // string value of it, but simply pass the object along - it will be serialized later as JSON...
                var propValue = prop.GetValue(entity);
                bool isLongDateTime = Attribute.GetCustomAttributes(prop).Any(a => a.GetType() == typeof(LongDateTimeAttribute));
                bool isLongDate = Attribute.GetCustomAttributes(prop).Any(a => a.GetType() == typeof(LongDateAttribute));
                object value = propValue.IsComplexType()
                    ? propValue
                    :
                        prop.PropertyType == typeof(DateTime?) || prop.PropertyType == typeof(DateTime)
                            ?
                                isLongDateTime
                                    ? propValue == null ? null : (object)Convert.ToInt64(Math.Floor(((DateTime)propValue).Subtract(new DateTime(1970, 1, 1)).TotalMilliseconds))
                                    :
                                        isLongDate
                                            ? propValue == null ? null : (object)Convert.ToInt64(Math.Floor(((DateTime)propValue).Date.Subtract(new DateTime(1970, 1, 1)).TotalMilliseconds))
                                            : propValue == null ? null : (object)((DateTime)propValue).ToString("yyyy-MM-dd")
                            :
                                prop.PropertyType == typeof(bool)
                                    ? propValue?.ToString().ToLowerInvariant()
                                    :
                                        prop.PropertyType == typeof(bool?)
                                            ? propValue?.ToString().ToLowerInvariant()
                                            : propValue?.ToString();

                var item = new HubspotDataEntityProp
                {
                    Property = propSerializedName,
                    Value = value
                };

                if (entity.IsNameValue)
                {
                    item.Property = null;
                    item.Name = propSerializedName;
                }
                if (item.Value == null) { continue; }

                mapped.Properties.Add(item);

                if (batchMode && prop.GetPropSerializedName() == "email")
                {
                    mapped.email = value;
                }
            }
            
            return mapped;
        }

        /// <summary>
        /// Convert from the dynamicly typed <see cref="ExpandoObject"/> into a strongly typed <see cref="IHubSpotModel"/>
        /// </summary>
        /// <param name="dynamicObject">The <see cref="ExpandoObject"/> representation of the returned json</param>
        /// <returns></returns>
        public T FromHubSpotResponse<T>(ExpandoObject dynamicObject) where T : IHubSpotModel, new()
        {
            var data = (T)ConvertSingleEntity(dynamicObject, new T());
            return data;
        }

        public T FromHubSpotListResponse<T>(ExpandoObject dynamicObject) where T : IHubSpotModel, new()
        {
            // get a handle to the underlying dictionary values of the ExpandoObject
            var expandoDict = (IDictionary<string, object>)dynamicObject;

            // For LIST contacts the "contacts" property should be populated, for LIST companies the "companies" property should be populated, and so on
            // in our T item, search for a property that is an IList<IHubSpotEntity> and use that as our prop name selector into the DynamoObject.....
            // So on the IContactListHubSpotEntity we have a IList<IHubSpotEntity> Contacts - find that prop, lowercase to contacts and that prop should
            // be in the DynamoObject from HubSpot! Tricky stuff
            var targetType = typeof(IHubSpotModel);
            var data = new T();
            var dataProps = data.GetType().GetProperties();
            var dataTargetProp = dataProps.SingleOrDefault(p => targetType.IsAssignableFrom(p.PropertyType.GenericTypeArguments.FirstOrDefault()));

            if (dataTargetProp == null)
            {
                throw new ArgumentException("Unable to locate a property on the data class that implements IList<T> where T is a IHubSpotEntity");
            }

            var propSerializedName = dataTargetProp.GetPropSerializedName();
            if (!expandoDict.ContainsKey(propSerializedName))
            {
                throw new ArgumentException($"The json data does not contain a property of name {propSerializedName} which is required to decode the json data");
            }

            // Find the generic type for the List in question
            var genericEntityType = dataTargetProp.PropertyType.GenericTypeArguments.First();
            // get a handle to Add on the list (actually from ICollection)
            var listAddMethod = dataTargetProp.PropertyType.FindMethodRecursively("Add", genericEntityType);
            // Condensed version of : https://stackoverflow.com/a/4194063/1662254
            var listInstance = Activator.CreateInstance(typeof(List<>).MakeGenericType(genericEntityType));
            if (listAddMethod == null)
            {
                throw new ArgumentException("Unable to locate Add method on the list of items to deserialize into - is it an IList?");
            }

            // Convert all the entities
            var jsonEntities = expandoDict[propSerializedName];
            foreach (var entry in jsonEntities as List<object>)
            {
                // convert single entity
                var expandoEntry = entry as ExpandoObject;
                var dto = ConvertSingleEntity(expandoEntry, Activator.CreateInstance(genericEntityType));
                // add entity to list
                listAddMethod.Invoke(listInstance, new[] { dto });
            }
            // assign our reflected list instance onto the data object
            dataTargetProp.SetValue(data, listInstance);

            var allPropNamesInSerializedFormat = GetAllPropsWithSerializedNameAsKey(dataProps);
            // Now try to map any remaining props from the dynamo object into the response dto we shall return
            foreach (var kvp in expandoDict)
            {
                // skip the property with all the items for the response as we have already mapped that
                if (kvp.Key == propSerializedName) continue;

                // The Key of the current item should be mapped, so we have to find a property in the target dto that "Serializes" into this value...
                if (!allPropNamesInSerializedFormat.TryGetValue(kvp.Key, out PropertyInfo theProp))
                {
                    continue;
                }
                // we have a property which name serializes to the kvp.Key, let's set the data

                // If theProp is a complex type we cannot just use SetValue, we need a conversion
                if (theProp.PropertyType.IsComplexType())
                {
                    var expandoEntry = kvp.Value as ExpandoObject;
                    var dto = ConvertSingleEntity(expandoEntry, Activator.CreateInstance(theProp.PropertyType));
                    theProp.SetValue(data, dto);
                }
                else // simple value type, assign it
                {
                    theProp.SetValue(data, kvp.Value);
                }
            }

            return data;
        }

        private IDictionary<string, PropertyInfo> GetAllPropsWithSerializedNameAsKey(PropertyInfo[] dataProps)
        {
            var dict = new Dictionary<string, PropertyInfo>();
            foreach (var prop in dataProps)
            {
                var propName = prop.GetPropSerializedName();
                dict.Add(propName, prop);
            }
            return dict;
        }

        /// <summary>
        /// Converts a single "dynamic" representation of an entity into a typed entity
        /// </summary>
        /// <remarks>
        /// The dynamic object being passed in should have a prop called "properties" which contains all the object properties to map, as well
        /// as vid and other root level objects stored in the HubSpot JSON response
        /// </remarks>
        /// <param name="dynamicObject">An <see cref="ExpandoObject"/> instance that contains a single HubSpot entity to deserialize</param>
        /// <param name="dto">An instantiated DTO that shall recieve data</param>
        /// <returns>The populated DTO</returns>
        internal object ConvertSingleEntity(ExpandoObject dynamicObject, object dto)
        {
            var expandoDict = (IDictionary<string, object>)dynamicObject;
            var dtoProps = dto.GetType().GetProperties(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);

            // The vid is the "id" of the entity
            if (expandoDict.TryGetValue("vid", out var vidData))
            {
                // TODO use properly serialized name of prop to find it
                var vidProp = dtoProps.SingleOrDefault(q => q.GetPropSerializedName() == "vid");
                vidProp?.SetValue(dto, vidData);
            }

            // The dealId is the "id" of the deal entity
            if (expandoDict.TryGetValue("dealId", out var dealIdData))
            {
                // TODO use properly serialized name of prop to find it
                var dealIdProp = dtoProps.SingleOrDefault(q => q.GetPropSerializedName() == "dealId");
                dealIdProp?.SetValue(dto, dealIdData);
            }
            // unless this is from the new v3 search api
            else if (dto is Api.Deal.Dto.DealHubSpotModel && expandoDict.TryGetValue("id", out dealIdData))
            {
                // TODO use properly serialized name of prop to find it
                var dealIdProp = dtoProps.SingleOrDefault(q => q.GetPropSerializedName() == "dealId");
                long? dealIdValue = null;
                if (dealIdData is string && dealIdData != null)
                    dealIdValue = Convert.ToInt64(dealIdData);
                dealIdProp?.SetValue(dto, dealIdValue);
            }

            // The companyId is the "id" of the company entity
            if (expandoDict.TryGetValue("companyId", out var companyIdData))
            {
                // TODO use properly serialized name of prop to find it
                var companyIdProp = dtoProps.SingleOrDefault(q => q.GetPropSerializedName() == "companyId");
                companyIdProp?.SetValue(dto, companyIdData);
            }
            else if (dto is Api.Company.Dto.CompanyHubSpotModel && expandoDict.TryGetValue("id", out companyIdData))
            {
                // TODO use properly serialized name of prop to find it
                var companyIdProp = dtoProps.SingleOrDefault(q => q.GetPropSerializedName() == "companyId");
                long? companyIdValue = null;
                if (companyIdData is string && companyIdData != null)
                    companyIdValue = Convert.ToInt64(companyIdData);
                companyIdProp?.SetValue(dto, companyIdValue);
            }

            // The taskId is the "id" of the task entity
            if (expandoDict.TryGetValue("taskId", out var taskIdData))
            {
                // TODO use properly serialized name of prop to find it
                var taskIdProp = dtoProps.SingleOrDefault(q => q.GetPropSerializedName() == "taskId");
                taskIdProp?.SetValue(dto, taskIdData);
            }
            else if (dto is Api.Task.Dto.TaskHubSpotModel && expandoDict.TryGetValue("id", out taskIdData))
            {
                // TODO use properly serialized name of prop to find it
                var taskIdProp = dtoProps.SingleOrDefault(q => q.GetPropSerializedName() == "taskId");
                long? taskIdValue = null;
                if (taskIdData is string && taskIdData != null)
                    taskIdValue = Convert.ToInt64(taskIdData);
                taskIdProp?.SetValue(dto, taskIdValue);
            }

            if (dto is Api.CustomObject.CustomObjectHubSpotModel && expandoDict.TryGetValue("id", out var idData)) 
            {
                // TODO use properly serialized name of prop to find it
                var idProp = dtoProps.SingleOrDefault(q => q.GetPropSerializedName() == "id");
                // id is string for custom objects so no need to convert.
                idProp?.SetValue(dto, idData);
            }

            // DateCreated
            if (expandoDict.TryGetValue("createdAt", out var createdAtData))
            {
                // TODO use properly serialized name of prop to find it
                var createdAtProp = dtoProps.SingleOrDefault(q => q.GetPropSerializedName() == "createdAt");
                createdAtProp?.SetValue(dto, createdAtData);
            }
            // DateUpdated
            if (expandoDict.TryGetValue("updatedAt", out var updatedAtData))
            {
                // TODO use properly serialized name of prop to find it
                var updatedAtProp = dtoProps.SingleOrDefault(q => q.GetPropSerializedName() == "updatedAt");
                updatedAtProp?.SetValue(dto, updatedAtData);
            }
            // DateUpdated
            if (expandoDict.TryGetValue("isDeleted", out var isDeletedData))
            {
                // TODO use properly serialized name of prop to find it
                var isDeletedProp = dtoProps.SingleOrDefault(q => q.GetPropSerializedName() == "isDeleted");
                isDeletedProp?.SetValue(dto, isDeletedData);
            }


            if (dto is PagingModel && expandoDict.TryGetValue("next", out var nextData))
            {
                // TODO use properly serialized name of prop to find it
                var nextProp = dtoProps.SingleOrDefault(q => q.GetPropSerializedName() == "next");

                var expandoEntry = nextData as ExpandoObject;
                var nextDto = ConvertSingleEntity(expandoEntry, Activator.CreateInstance(nextProp.PropertyType));
                nextProp?.SetValue(dto, nextDto);
            }
            else if (dto is NextModel && expandoDict.TryGetValue("after", out var afterData))
            {
                // TODO use properly serialized name of prop to find it
                var afterProp = dtoProps.SingleOrDefault(q => q.GetPropSerializedName() == "after");
                afterProp?.SetValue(dto, afterData);
            }

            // The Properties object in the json / response data contains all the props we wish to map - if that does not exist
            // we cannot proceeed
            if (!expandoDict.TryGetValue("properties", out var dynamicProperties)) return dto;

            foreach (var dynamicProp in dynamicProperties as ExpandoObject)
            {
                // prop.Key contains the name of the property we wish to map into the DTO
                // prop.Value contains the data returned by HubSpot, which is also an object 
                // in there we need to go get the "value" prop to get the actual value

                IDictionary<string, Object> dynamicPropValue = dynamicProp.Value as IDictionary<string, Object>;
                object dynamicValue;
                if (dynamicPropValue != null)
                {
                    if (!dynamicPropValue.TryGetValue("value", out dynamicValue))
                    {
                        continue;
                    }
                }
                // not all responses are dictionaries but are sometimes nicely formatted objects
                else
                {
                    dynamicValue = dynamicProp.Value;
                }

                // TODO use properly serialized name of prop to find and set it's value
                var targetProp = dtoProps.SingleOrDefault(q => q.GetPropSerializedName() == dynamicProp.Key);

                if (targetProp != null)
                {
                    var isNullable = targetProp.PropertyType.Name.Contains("Nullable") || targetProp.PropertyType == typeof(string);

                    var type = Nullable.GetUnderlyingType(targetProp.PropertyType) ?? targetProp.PropertyType;

                    // resolves issue where if the object property was a nullable number (int/double/etc.) and the value being processed
                    // was null or an empty string an exception 'string was not in the correct format' occurred.
                    // see https://github.com/squaredup/HubSpot.NET/pull/8
                    if (isNullable && (dynamicValue?.ToString()).IsNullOrEmpty())
                    {
                        // if nullable and the value to convert is null or an empty string it should not be converted
                        targetProp.SetValue(dto, null);
                    }
                    else
                    {
                        var value = dynamicValue.GetType() == type
                            ? dynamicValue
                            : (dynamicValue is long) && type == typeof(DateTime)
                                ? new DateTime(1970, 1, 1).AddMilliseconds((long)dynamicValue)
                                : (dynamicValue is string && long.TryParse((string)dynamicValue, out long dynamicLongValue)) && type == typeof(DateTime)
                                    ? new DateTime(1970, 1, 1).AddMilliseconds((long)dynamicLongValue)
                                    : (dynamicValue is string) && typeof(string[]) == type
                                        ? Convert.ChangeType(((string)dynamicValue)?.Split(';'), type)
                                        : (dynamicValue is string) && (typeof(IList<string>).IsAssignableFrom(type) || typeof(IEnumerable<string>) == type)
                                            ? Convert.ChangeType(((string)dynamicValue)?.Split(';').ToList(), type)
                                            : Convert.ChangeType(dynamicValue, type);
                        targetProp.SetValue(dto, value);
                    }
                }
            }
            return dto;
        }
    }
}
