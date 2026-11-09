using System;
using System.Collections.Generic;
using System.Linq;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse.UE4.Assets.Objects.Properties;

namespace CUE4ParseCLI.Core
{
    /// <summary>
    /// Represents detailed metadata about a UE class for database schema generation
    /// </summary>
    public class ClassMetadataInfo
    {
        public string ClassName { get; set; } = "";
        public string PackagePath { get; set; } = "";
        public string ParentClass { get; set; } = "";
        public string ClassType { get; set; } = "";
        public List<PropertyMetadata> Properties { get; set; } = new();
        public Dictionary<string, object> DefaultValues { get; set; } = new();
        public DateTime ExtractedAt { get; set; }
    }

    /// <summary>
    /// Metadata about a single property in a class
    /// </summary>
    public class PropertyMetadata
    {
        public string Name { get; set; } = "";
        public string Type { get; set; } = "";
        public object? DefaultValue { get; set; }
        public bool IsArray { get; set; }
    }

    /// <summary>
    /// Database schema summary for generating CREATE TABLE statements
    /// </summary>
    public class ClassSchemaSummary
    {
        public string TableName { get; set; } = "";
        public string ParentTable { get; set; } = "";
        public List<ColumnDefinition> Columns { get; set; } = new();
    }

    public class ColumnDefinition
    {
        public string Name { get; set; } = "";
        public string SqlType { get; set; } = "";
        public bool IsNullable { get; set; }
        public string? DefaultValue { get; set; }
    }

    /// <summary>
    /// Utility class for extracting class metadata from UE exports
    /// </summary>
    public static class ClassMetadataExtractor
    {
        public static ClassMetadataInfo? ExtractClassMetadata(UObject export)
        {
            if (export == null) return null;

            var metadata = new ClassMetadataInfo
            {
                ClassName = export.Name,
                PackagePath = export.Owner?.Name ?? "",
                ParentClass = GetParentClassName(export),
                ClassType = DetermineClassType(export),
                Properties = ExtractProperties(export),
                DefaultValues = ExtractDefaultValues(export),
                ExtractedAt = DateTime.UtcNow
            };

            return metadata;
        }

        private static string GetParentClassName(UObject export)
        {
            try
            {
                if (export.Class != null)
                {
                    return export.Class.Name.ToString();
                }
                if (export.Super != null)
                {
                    return export.Super.Name.ToString();
                }
            }
            catch
            {
                // Fallback
            }
            return "UObject";
        }

        private static string DetermineClassType(UObject export)
        {
            try
            {
                var typeName = export.ExportType;
                if (typeName.Contains("Blueprint")) return "BlueprintClass";
                if (typeName.Contains("DataAsset")) return "DataAsset";
                
                if (export.Class != null)
                {
                    var className = export.Class.Name.ToString();
                    if (className.Contains("/Script/")) return "NativeClass";
                }
            }
            catch
            {
                // Fallback
            }
            return export.ExportType;
        }

        private static List<PropertyMetadata> ExtractProperties(UObject export)
        {
            var properties = new List<PropertyMetadata>();
            if (export.Properties == null) return properties;

            foreach (var prop in export.Properties)
            {
                try
                {
                    var propType = prop.PropertyType != null ? prop.PropertyType.Text : "Unknown";
                    properties.Add(new PropertyMetadata
                    {
                        Name = prop.Name.Text,
                        Type = propType,
                        IsArray = prop.ArrayIndex > 0,
                        DefaultValue = ExtractSimpleValue(prop.Tag)
                    });
                }
                catch
                {
                    // Skip problematic properties
                }
            }

            return properties;
        }

        private static Dictionary<string, object> ExtractDefaultValues(UObject export)
        {
            var defaults = new Dictionary<string, object>();
            if (export.Properties == null) return defaults;

            foreach (var prop in export.Properties)
            {
                try
                {
                    var value = ExtractSimpleValue(prop.Tag);
                    if (value != null)
                    {
                        defaults[prop.Name.Text] = value;
                    }
                }
                catch
                {
                    // Skip
                }
            }

            return defaults;
        }

        private static object? ExtractSimpleValue(FPropertyTagType? tagType)
        {
            if (tagType == null) return null;

            try
            {
                // Check if it's an FName type by checking the actual type
                if (tagType.GetType().Name == "FName")
                {
                    // Access the Text property via reflection since we can't cast
                    var textProp = tagType.GetType().GetProperty("Text");
                    if (textProp != null)
                        return textProp.GetValue(tagType);
                }

                var type = tagType.GetType();
                var valueProp = type.GetProperty("Value");
                if (valueProp != null)
                    return valueProp.GetValue(tagType);

                return tagType.ToString();
            }
            catch
            {
                return null;
            }
        }

        public static ClassSchemaSummary ExtractSchemaSummary(ClassMetadataInfo metadata)
        {
            return new ClassSchemaSummary
            {
                TableName = SanitizeTableName(metadata.ClassName),
                ParentTable = SanitizeTableName(metadata.ParentClass),
                Columns = metadata.Properties.Select(p => new ColumnDefinition
                {
                    Name = p.Name,
                    SqlType = MapToSqlType(p.Type, p.IsArray),
                    IsNullable = false,
                    DefaultValue = p.DefaultValue?.ToString()
                }).ToList()
            };
        }

        private static string SanitizeTableName(string className)
        {
            className = className.TrimStart('A', 'U', 'F', 'S', 'T', 'E');
            if (className.StartsWith("BP_"))
                className = className.Substring(3);
            return className;
        }

        private static string MapToSqlType(string ueType, bool isArray)
        {
            if (isArray) return "JSON";

            return ueType.ToLower() switch
            {
                var t when t.Contains("int") => "INTEGER",
                var t when t.Contains("float") || t.Contains("double") => "REAL",
                var t when t.Contains("bool") => "BOOLEAN",
                var t when t.Contains("string") || t.Contains("name") || t.Contains("text") => "TEXT",
                _ => "TEXT"
            };
        }
    }
}
