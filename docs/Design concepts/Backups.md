# Backups in Grace

Like any computer system, Grace needs to have backups, and to test restoration from those backups.

Because Grace's data is stored in both a database (like Cosmos DB, Cassandra, etc.) and in an object storage system (like Azure Blob Storage, or S3), we need to have a way to define what a "snapshot" is across those systems, and we also probably need to understand that it's possible that the last few seconds before some failure occurs may involve losing some data. Over time, we'll chaos-test Grace and see if we can minimize the amount of data loss.

The thing is... Grace can run on many different platforms, and each platform has its own backup and restore mechanisms. Every Grace service provider, and everyone self-hosting Grace, will have to decide how to do backups and restores.

With that said, there are some general perspectives that need to be kept in mind when deciding what it means to have a backup of Grace.

## Geo-redundancy

The first, obvious form of "backup" is to use geo-redundant features of the underlying storage systems. Because I'm an Azure guy, I'll use Azure examples; I know similar features exist in AWS / GCP / etc.

Azure Cosmos DB can synchronize data between many regions around the world within seconds, just through configuration. Azure Blob Storage has a geo-redundant option that pairs your main region with a backup region at least several hundred kilometers away. It's basically malpractice to not use these features when they're available with a click.

## Database backups

Whatever form of backup for the actor storage database you choose, it should be possible to restore the database to a point in time.

If you have the exact time of the backup, you can use that to filter any changes to object storage after that time in the event of failure and restoration.

Over time, we expect to create, or to have Grace service providers create and share, mature utilities to assist in handling all of this.

## User backups

Although many users will be comfortable with the idea that their Grace service provider is adequately backing up their repositories, some will want to have their own backups. There is, of course, a comfort in having every Git repository instance be a full copy. This is especially true for users who are self-hosting Grace. They may want to have a backup of their repositories in a different region, or even in a different cloud provider.

### Git bundle file

Grace will support exporting a repository to Git bundle format. Because the creation and maintenance of those files can be compute-intensive, we'd prefer to have this done infrequently... maybe only on promotion. We'll have to have a way to do this on-demand, of course.

### Object storage backups

Users will want copies of the files in object storage. Those copies can be done either by the Grace service provider, or by the user.

If a user wants to subscribe to Grace's event stream, they can use that to keep their own object storage in sync with the Grace service provider's object storage, all the way down to saves. Whenever an event happens that they feel they want to make sure they have a backup for - at least every promotion - they can copy the correct versions of files from object storage to their own object storage. Grace service providers will have to decide how to handle this, how to charge for network egress, etc.

Alternatively, a Grace service provider may offer a service themselves where a user can supply their own object storage credentials, and the service provider will copy the files to the user's object storage. Again, there are concerns about how to charge for the service, and how to charge for network egress.
