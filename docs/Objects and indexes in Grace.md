# Objects and indexes[^1] in Grace

## Introduction

As a centralized version control system, Grace keeps metadata about directories in a document database, and versions of files in object storage. Grace also has to keep track of those directories and files in the developer's working directory, and in the /.grace/objects directory. This document explains how all of that happens.

## Server data and object storage

Directories are stored as records in a document database, as _directory versions_. A directory version consists of:

- A DirectoryId (Guid)
- A relative path (relative to the repository root) (string)
- A SHA-256 value that identfies the unique version of this directory (string)
- The contents of the directory
    - A list of subdirectories' DirectoryId's
    - A list of files, as FileVersion's (see below)
- The size of the files in the directory
- The recursive size of the files in the directory and in all subdirectories
- Other metadata

Files are stored as blobs in object storage. Files are renamed to include the SHA-256 value in the name. For instance, `myfile.ts` would be renamed to `myfile_[SHA256 hash].ts`.

[^1]: Yes, I know that the plural of "index" is "indices". I like "indexes" better.