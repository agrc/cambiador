# cambiador

esri geodatabase change detection for sql server with a spatial data type

![cambiador_sm](https://user-images.githubusercontent.com/325813/90076030-fd024180-dcbb-11ea-84b3-f18c825a3231.png)

## prerequisites

1. Enterprise SQL Server with the spatial data type and tables registered with the geodatabase
1. A table named `ChangeDetection` that is not registered with the geodatabase

   ```sql
      CREATE TABLE [META].[ChangeDetection](
      [table_name] [nvarchar](250) NULL,
      [last_modified] [date] NULL,
      [hash] [nvarchar](50) NULL,
      [id] [int] IDENTITY(1,1) NOT NULL
   )
   ```

## how it works

The first time the tool is run it will update the `ChangeDetection` table with the table name, the current date, and a hash digest of all of the rows hashes combine. On subsequent runs, it will query the `ChangeDetection` table for the single hash, rehash the table, compare the results, and then update the `last_modified` and `hash` values on the `ChangeDetection` table if changes were found.

## how to use

Applications and users that are interested in taking action when data changes can query the `ChangeDetection` table and inspect the dates that are greater than the last time they checked. For instance, if a process is scheduled to run daily, querying for `last_modified` dates that equal the current date would result in the set of data to process.

## running in development

`dotnet run cambiador`

## deployment

### build

1. edit the `appsettings.json` file for the database connection
1. bump the version in the `cambiador.csproj`
1. build the project into a single file
   `dotnet publish --runtime win-x64 -c Release /p:PublishSingleFile=true /p:DebugType=None --no-self-contained`
1. deploy the `cambiador.exe` file that will be created in `bin/release/net9.0/win-64/publish` to the place where it will run
