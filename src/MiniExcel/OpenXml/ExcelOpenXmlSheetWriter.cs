﻿using MiniExcelLibs.Utils;
using MiniExcelLibs.Zip;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;


namespace MiniExcelLibs.OpenXml
{

    internal partial class ExcelOpenXmlSheetWriter : IExcelWriter
    {
        private readonly MiniExcelZipArchive _archive;
        private readonly static UTF8Encoding _utf8WithBom = new System.Text.UTF8Encoding(true);
        private readonly OpenXmlConfiguration _configuration;
        private readonly Stream _stream;
        private readonly bool _printHeader;
        private readonly object _value;
        private readonly List<string> _sheetNames = new List<string>();
        public ExcelOpenXmlSheetWriter(Stream stream,object value, string sheetName, IConfiguration configuration, bool printHeader)
        {
            this._stream = stream;
            this._archive = new MiniExcelZipArchive(_stream, ZipArchiveMode.Create, true, _utf8WithBom);
            this._configuration = configuration as OpenXmlConfiguration ?? OpenXmlConfiguration.DefaultConfig;
            this._printHeader = printHeader;
            this._value = value;
            _sheetNames.Add(sheetName); //TODO:remove
        }

        public void SaveAs()
        {
            GenerateDefaultOpenXml();
            {
                if (_value is IDictionary<string, object>)
                {
                    var sheetId = 0;
                    var sheets = _value as IDictionary<string, object>;
                    _sheetNames.RemoveAt(0);//TODO:remove
                    foreach (var sheet in sheets)
                    {
                        sheetId++;
                        var sheetPath = $"xl/worksheets/sheet{sheetId}.xml";
                        CreateSheetXml(sheet.Value, sheetPath);
                        _sheetNames.Add(sheet.Key);
                    }
                }
                else if (_value is DataSet)
                {
                    var sheetId = 0;
                    var sheets = _value as DataSet;
                    _sheetNames.RemoveAt(0);//TODO:remove
                    foreach (DataTable dt in sheets.Tables)
                    {
                        _sheetNames.Add(dt.TableName);
                    }
                    
                    foreach (DataTable dt in sheets.Tables)
                    {
                        sheetId++;
                        var sheetPath = $"xl/worksheets/sheet{sheetId}.xml";
                        CreateSheetXml(dt,sheetPath);
                    }
                }
                else
                {
                    var sheetPath = "xl/worksheets/sheet1.xml";
                    CreateSheetXml(_value,sheetPath);
                }
            }
            GenerateEndXml();
            _archive.Dispose();
        }

        private void CreateSheetXml(object value,string sheetPath)
        {
            ZipArchiveEntry entry = _archive.CreateEntry(sheetPath);
            using (var zipStream = entry.Open())
            using (StreamWriter writer = new StreamWriter(zipStream, _utf8WithBom))
            {
                if (value == null)
                {
                    WriteEmptySheet(writer);
                    goto End; //for re-using code
                }

                var type = value.GetType();

                Type genericType = null;

                //DapperRow

                if (value is IDataReader)
                {
                    GenerateSheetByIDataReader(writer, value as IDataReader);
                }
                else if (value is IEnumerable)
                {
                    var values = value as IEnumerable;

                    // try to get type from reflection
                    // genericType = null

                    var rowCount = 0;

                    var maxColumnIndex = 0;
                    List<object> keys = new List<object>();
                    List<ExcelCustomPropertyInfo> props = null;
                    string mode = null;

                    // reason : https://stackoverflow.com/questions/66797421/how-replace-top-format-mark-after-streamwriter-writing
                    // check mode & get maxRowCount & maxColumnIndex
                    {
                        foreach (var item in values) //TODO: need to optimize
                        {
                            rowCount = checked(rowCount + 1);

                            //TODO: if item is null but it's collection<T>, it can get T type from reflection
                            if (item != null && mode == null)
                            {
                                if (item is IDictionary<string, object>)
                                {
                                    var item2 = item as IDictionary<string, object>;
                                    mode = "IDictionary<string, object>";
                                    maxColumnIndex = item2.Keys.Count;
                                    foreach (var key in item2.Keys)
                                        keys.Add(key);
                                }
                                else if (item is IDictionary)
                                {
                                    var item2 = item as IDictionary;
                                    mode = "IDictionary";
                                    maxColumnIndex = item2.Keys.Count;
                                    foreach (var key in item2.Keys)
                                        keys.Add(key);
                                }
                                else
                                {
                                    var _t = item.GetType();
                                    if (_t != genericType)
                                        genericType = item.GetType();
                                    genericType = item.GetType();
                                    SetGenericTypePropertiesMode(genericType, ref mode, out maxColumnIndex, out props);
                                }

                                var collection = value as ICollection;
                                if (collection != null)
                                {
                                    rowCount = checked((value as ICollection).Count);
                                    break;
                                }
                                continue;
                            }
                        }
                    }

                    if (rowCount == 0)
                    {
                        // only when empty IEnumerable need to check this issue #133  https://github.com/shps951023/MiniExcel/issues/133
                        genericType = TypeHelper.GetGenericIEnumerables(values).FirstOrDefault();
                        if (genericType == null || genericType == typeof(object) // sometime generic type will be object, e.g: https://user-images.githubusercontent.com/12729184/132812859-52984314-44d1-4ee8-9487-2d1da159f1f0.png
                            || typeof(IDictionary<string, object>).IsAssignableFrom(genericType)
                            || typeof(IDictionary).IsAssignableFrom(genericType))
                        {
                            WriteEmptySheet(writer);
                            goto End; //for re-using code
                        }
                        else
                        {
                            SetGenericTypePropertiesMode(genericType, ref mode, out maxColumnIndex, out props);
                        }
                    }

                    writer.Write($@"<?xml version=""1.0"" encoding=""utf-8""?><x:worksheet xmlns:x=""http://schemas.openxmlformats.org/spreadsheetml/2006/main"">");

                    // dimension 
                    var maxRowIndex = rowCount + (_printHeader && rowCount > 0 ? 1 : 0);  //TODO:it can optimize
                    writer.Write($@"<x:dimension ref=""{GetDimensionRef(maxRowIndex, maxColumnIndex)}""/>");

                    //cols:width
                    var ecwProp = props?.Where(x => x?.ExcelColumnWidth != null).ToList();
                    if (ecwProp != null && ecwProp.Count > 0)
                    {
                        writer.Write($@"<x:cols>");
                        foreach (var p in ecwProp)
                        {
                            writer.Write($@"<x:col min=""{p.ExcelColumnIndex + 1}"" max=""{p.ExcelColumnIndex + 1}"" width=""{p.ExcelColumnWidth}"" customWidth=""1"" />");
                        }
                        writer.Write($@"</x:cols>");
                    }

                    //header
                    writer.Write($@"<x:sheetData>");
                    var yIndex = 1;
                    var xIndex = 1;
                    if (_printHeader)
                    {
                        var cellIndex = xIndex;
                        writer.Write($"<x:row r=\"{yIndex.ToString()}\">");
                        if (props != null)
                        {
                            foreach (var p in props)
                            {
                                if (p == null)
                                {
                                    cellIndex++; //reason : https://github.com/shps951023/MiniExcel/issues/142
                                    continue;
                                }

                                var r = ExcelOpenXmlUtils.ConvertXyToCell(cellIndex, yIndex);
                                WriteC(writer, r, columnName: p.ExcelColumnName);
                                cellIndex++;
                            }
                        }
                        else
                        {
                            foreach (var key in keys)
                            {
                                var r = ExcelOpenXmlUtils.ConvertXyToCell(cellIndex, yIndex);
                                WriteC(writer, r, columnName: key.ToString());
                                cellIndex++;
                            }
                        }
                        writer.Write($"</x:row>");
                        yIndex++;
                    }

                    // body
                    if (mode == "IDictionary<string, object>") //Dapper Row
                        GenerateSheetByDapperRow(writer, value as IEnumerable, rowCount, keys.Cast<string>().ToList(), xIndex, yIndex);
                    else if (mode == "IDictionary") //IDictionary
                        GenerateSheetByIDictionary(writer, value as IEnumerable, rowCount, keys, xIndex, yIndex);
                    else if (mode == "Properties")
                        GenerateSheetByProperties(writer, value as IEnumerable, props, rowCount, xIndex, yIndex);
                    else
                        throw new NotImplementedException($"Type {type.Name} & genericType {genericType.Name} not Implemented. please issue for me.");
                    writer.Write("</x:sheetData>");
                    if (_configuration.AutoFilter)
                        writer.Write($"<x:autoFilter ref=\"A1:{ExcelOpenXmlUtils.ConvertXyToCell(maxColumnIndex, maxRowIndex == 0 ? 1 : maxRowIndex)}\" />");
                    writer.Write("</x:worksheet>");
                }
                else if (value is DataTable)
                {
                    GenerateSheetByDataTable(writer, value as DataTable);
                }
                else
                {
                    throw new NotImplementedException($"Type {type.Name} & genericType {genericType.Name} not Implemented. please issue for me.");
                }
            }
        End: //for re-using code
            _zipDictionary.Add(sheetPath, new ZipPackageInfo(entry, "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"));
        }

        private static void SetGenericTypePropertiesMode(Type genericType, ref string mode, out int maxColumnIndex, out List<ExcelCustomPropertyInfo> props)
        {
            mode = "Properties";
            if (genericType.IsValueType)
                throw new NotImplementedException($"MiniExcel not support only {genericType.Name} value generic type");
            else if (genericType == typeof(string) || genericType == typeof(DateTime) || genericType == typeof(Guid))
                throw new NotImplementedException($"MiniExcel not support only {genericType.Name} generic type");
            props = CustomPropertyHelper.GetSaveAsProperties(genericType);
            maxColumnIndex = props.Count;
        }

        private void WriteEmptySheet(StreamWriter writer)
        {
            writer.Write($@"<?xml version=""1.0"" encoding=""utf-8""?><x:worksheet xmlns:x=""http://schemas.openxmlformats.org/spreadsheetml/2006/main""><x:dimension ref=""A1""/><x:sheetData></x:sheetData></x:worksheet>");
        }

        private void GenerateSheetByDapperRow(StreamWriter writer, IEnumerable value, int rowCount, List<string> keys, int xIndex = 1, int yIndex = 1)
        {
            foreach (IDictionary<string, object> v in value)
            {
                writer.Write($"<x:row r=\"{yIndex.ToString()}\">");
                var cellIndex = xIndex;
                foreach (var key in keys)
                {
                    var cellValue = v[key];
                    WriteCell(writer, yIndex, cellIndex, cellValue, null);
                    cellIndex++;
                }
                writer.Write($"</x:row>");
                yIndex++;
            }
        }

        private void GenerateSheetByIDictionary(StreamWriter writer, IEnumerable value, int rowCount, List<object> keys, int xIndex = 1, int yIndex = 1)
        {
            foreach (IDictionary v in value)
            {
                writer.Write($"<x:row r=\"{yIndex.ToString()}\">");
                var cellIndex = xIndex;
                foreach (var key in keys)
                {
                    var cellValue = v[key];
                    WriteCell(writer, yIndex, cellIndex, cellValue, null);
                    cellIndex++;
                }
                writer.Write($"</x:row>");
                yIndex++;
            }
        }

        private void GenerateSheetByProperties(StreamWriter writer, IEnumerable value, List<ExcelCustomPropertyInfo> props, int rowCount, int xIndex = 1, int yIndex = 1)
        {
            foreach (var v in value)
            {
                writer.Write($"<x:row r=\"{yIndex.ToString()}\">");
                var cellIndex = xIndex;
                foreach (var p in props)
                {
                    if (p == null) //reason:https://github.com/shps951023/MiniExcel/issues/142
                    {
                        cellIndex++;
                        continue;
                    }
                    var cellValue = p.Property.GetValue(v);
                    WriteCell(writer, yIndex, cellIndex, cellValue, p);
                    cellIndex++;
                }
                writer.Write($"</x:row>");
                yIndex++;
            }
        }

        private static void WriteCell(StreamWriter writer, int yIndex, int cellIndex, object value, ExcelCustomPropertyInfo p)
        {
            var v = string.Empty;
            var t = "str";
            var s = "2";
            if (value == null)
            {
                v = "";
            }
            else if (value is string)
            {
                v = ExcelOpenXmlUtils.EncodeXML(value.ToString());
            }
            else
            {
                Type type = null;
                if (p == null)
                {
                    type = value.GetType();
                    type = Nullable.GetUnderlyingType(type) ?? type;
                }
                else
                {
                    type = p.ExcludeNullableType; //sometime it doesn't need to re-get type like prop
                }

                if (type.IsEnum)
                {
                    t = "str";
                    var description = CustomPropertyHelper.DescriptionAttr(type, value); //TODO: need to optimze
                    if (description != null)
                        v = description;
                    else
                        v = value.ToString();
                }
                else if (TypeHelper.IsNumericType(type))
                {
                    t = "n";
                    v = value.ToString();
                }
                else if (type == typeof(bool))
                {
                    t = "b";
                    v = (bool)value ? "1" : "0";
                }
                else if (type == typeof(DateTime))
                {
                    if (p == null || p.ExcelFormat == null)
                    {
                        t = null;
                        s = "3";
                        v = ((DateTime)value).ToOADate().ToString();
                    }
                    else
                    {
                        t = "str";
                        v = ((DateTime)value).ToString(p.ExcelFormat);
                    }
                }
                else
                {
                    v = ExcelOpenXmlUtils.EncodeXML(value.ToString());
                }
            }

            var columname = ExcelOpenXmlUtils.ConvertXyToCell(cellIndex, yIndex);
            if (v != null && (v.StartsWith(" ") || v.EndsWith(" "))) /*Prefix and suffix blank space will lost after SaveAs #294*/
                writer.Write($"<x:c r=\"{columname}\" {(t == null ? "" : $"t =\"{t}\"")} s=\"{s}\" xml:space=\"preserve\"><x:v>{v}</x:v></x:c>");
            else
                //t check avoid format error ![image](https://user-images.githubusercontent.com/12729184/118770190-9eee3480-b8b3-11eb-9f5a-87a439f5e320.png)
                writer.Write($"<x:c r=\"{columname}\" {(t == null ? "" : $"t =\"{t}\"")} s=\"{s}\"><x:v>{v}</x:v></x:c>");
        }

        private void GenerateSheetByDataTable(StreamWriter writer, DataTable value)
        {
            var xy = ExcelOpenXmlUtils.ConvertCellToXY("A1");

            //GOTO Top Write:
            writer.Write($@"<?xml version=""1.0"" encoding=""utf-8""?><x:worksheet xmlns:x=""http://schemas.openxmlformats.org/spreadsheetml/2006/main"">");
            {
                var yIndex = xy.Item2;

                // dimension
                var maxRowIndex = value.Rows.Count + (_printHeader && value.Rows.Count > 0 ? 1 : 0);
                var maxColumnIndex = value.Columns.Count;
                writer.Write($@"<x:dimension ref=""{GetDimensionRef(maxRowIndex, maxColumnIndex)}""/><x:sheetData>");

                if (_printHeader)
                {
                    writer.Write($"<x:row r=\"{yIndex.ToString()}\">");
                    var xIndex = xy.Item1;
                    foreach (DataColumn c in value.Columns)
                    {
                        var r = ExcelOpenXmlUtils.ConvertXyToCell(xIndex, yIndex);
                        WriteC(writer, r, columnName: c.Caption ?? c.ColumnName);
                        xIndex++;
                    }
                    writer.Write($"</x:row>");
                    yIndex++;
                }

                for (int i = 0; i < value.Rows.Count; i++)
                {
                    writer.Write($"<x:row r=\"{yIndex.ToString()}\">");
                    var xIndex = xy.Item1;

                    for (int j = 0; j < value.Columns.Count; j++)
                    {
                        var cellValue = value.Rows[i][j];
                        WriteCell(writer, yIndex, xIndex, cellValue, null);
                        xIndex++;
                    }
                    writer.Write($"</x:row>");
                    yIndex++;
                }
            }
            writer.Write("</x:sheetData></x:worksheet>");
        }

        private void GenerateSheetByIDataReader(StreamWriter writer, IDataReader reader)
        {
            var xy = ExcelOpenXmlUtils.ConvertCellToXY("A1");

            writer.Write($@"<?xml version=""1.0"" encoding=""utf-8""?><x:worksheet xmlns:x=""http://schemas.openxmlformats.org/spreadsheetml/2006/main"">");
            var yIndex = xy.Item2;
            var xIndex = 0;
            {
                // TODO: dimension
                //var maxRowIndex = value.Rows.Count + (printHeader && value.Rows.Count > 0 ? 1 : 0);
                //var maxColumnIndex = value.Columns.Count;
                //writer.Write($@"<x:dimension ref=""{GetDimensionRef(maxRowIndex, maxColumnIndex)}""/>");
                writer.Write("<x:sheetData>");
                int fieldCount = reader.FieldCount;
                if (_printHeader)
                {
                    writer.Write($"<x:row r=\"{yIndex.ToString()}\">");
                    xIndex = xy.Item1;
                    for (int i = 0; i < fieldCount; i++)
                    {
                        var r = ExcelOpenXmlUtils.ConvertXyToCell(xIndex, yIndex);
                        var columnName = reader.GetName(i);
                        WriteC(writer, r, columnName);
                        xIndex++;
                    }
                    writer.Write($"</x:row>");
                    yIndex++;
                }

                while (reader.Read())
                {
                    writer.Write($"<x:row r=\"{yIndex.ToString()}\">");
                    xIndex = xy.Item1;

                    for (int i = 0; i < fieldCount; i++)
                    {
                        var cellValue = reader.GetValue(i);
                        WriteCell(writer, yIndex, xIndex, cellValue, null);
                        xIndex++;
                    }
                    writer.Write($"</x:row>");
                    yIndex++;
                }
            }
            writer.Write("</x:sheetData>");
            writer.Write($"<x:autoFilter ref=\"A1:{ExcelOpenXmlUtils.ConvertXyToCell(xIndex, yIndex)}\" />");
            writer.Write("</x:worksheet>");
        }

        private static void WriteC(StreamWriter writer, string r, string columnName)
        {
            writer.Write($"<x:c r=\"{r}\" t=\"str\" s=\"1\">");
            writer.Write($"<x:v>{ExcelOpenXmlUtils.EncodeXML(columnName)}"); //issue I45TF5
            writer.Write($"</x:v>");
            writer.Write($"</x:c>");
        }

        private void GenerateEndXml()
        {
            
            // styles.xml 
            {
                var styleXml = string.Empty;

                if (_configuration.TableStyles == TableStyles.None)
                {
                    styleXml = _noneStylesXml;
                }
                else if (_configuration.TableStyles == TableStyles.Default)
                {
                    styleXml = _defaultStylesXml;
                }
                CreateZipEntry(@"xl/styles.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml", styleXml);
            }

            // workbook.xml 、 workbookRelsXml
            {
                var workbookXml = new StringBuilder();
                var workbookRelsXml = new StringBuilder();

                var sheetId = 0;
                foreach (var sheetName in _sheetNames)
                {
                    sheetId++;
                    var id = $"R{Guid.NewGuid().ToString("N")}";
                    workbookXml.AppendLine($@"<x:sheet xmlns:r=""http://schemas.openxmlformats.org/officeDocument/2006/relationships"" name=""{sheetName}"" sheetId=""{sheetId}"" r:id=""{id}"" />");
                    workbookRelsXml.AppendLine($@"<Relationship Type=""http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet"" Target=""/xl/worksheets/sheet{sheetId}.xml"" Id=""{id}"" />");
                }
                CreateZipEntry(@"xl/workbook.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml",
                    ExcelOpenXmlSheetWriter._defaultWorkbookXml.Replace("{{sheets}}", workbookXml.ToString()));
                CreateZipEntry(@"xl/_rels/workbook.xml.rels", "application/vnd.openxmlformats-package.relationships+xml",
                    ExcelOpenXmlSheetWriter._defaultWorkbookXmlRels.Replace("{{sheets}}", workbookRelsXml.ToString()));
            }

            //[Content_Types].xml 
            var sb = new StringBuilder(@"<?xml version=""1.0"" encoding=""UTF-8"" standalone=""yes""?><Types xmlns=""http://schemas.openxmlformats.org/package/2006/content-types""><Default ContentType=""application/xml"" Extension=""xml""/><Default ContentType=""application/vnd.openxmlformats-package.relationships+xml"" Extension=""rels""/>");
            foreach (var p in _zipDictionary)
                sb.Append($"<Override ContentType=\"{p.Value.ContentType}\" PartName=\"/{p.Key}\" />");
            sb.Append("</Types>");

            ZipArchiveEntry entry = _archive.CreateEntry("[Content_Types].xml");
            using (var zipStream = entry.Open())
            using (StreamWriter writer = new StreamWriter(zipStream, _utf8WithBom))
                writer.Write(sb.ToString());
        }

        private string GetDimensionRef(int maxRowIndex, int maxColumnIndex)
        {
            string dimensionRef;
            if (maxRowIndex == 0 && maxColumnIndex == 0)
                dimensionRef = "A1";
            else if (maxColumnIndex == 1)
                dimensionRef = $"A{maxRowIndex}";
            else if (maxRowIndex == 0)
                dimensionRef = $"A1:{ColumnHelper.GetAlphabetColumnName(maxColumnIndex - 1)}1";
            else
                dimensionRef = $"A1:{ColumnHelper.GetAlphabetColumnName(maxColumnIndex - 1)}{maxRowIndex}";
            return dimensionRef;
        }

        public Task SaveAsAsync()
        {
            return Task.Run(() => SaveAs());
        }
    }
}
