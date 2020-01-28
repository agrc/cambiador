# cambiador

esri geodatabase change detection for sql server with a spatial data type

## prerequisites

1. Enterprise SQL Server with the spatial data type and tables registered with the geodatabase
1. A table named `ChangeDetection` that is not registered with the geodatabase

   1. `table_name` as `nvarchar(something)`
   1. `last_modified` as `date` type
   1. `hash` as `nvarchar(something)`

## how it works

The first time the tool is run it will update the `ChangeDetection` table with the table name, the current date, and a hash digest of all of the rows hashes combine. On subsequent runs, it will query the `ChangeDetection` table for the single hash, rehash the table, compare the results, and then update the `last_modified` and `hash` values on the `ChangeDetection` table if changes were found.

## how to use

Applications and users that are interested in taking action when data changes can query the `ChangeDetection` table and inspect the dates that are greater than the last time they checked. For instance, if a process is scheduled to run daily, querying for `last_modified` dates that equal the current date would result in the set of data to process.
