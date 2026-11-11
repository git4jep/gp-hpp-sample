SET XACT_ABORT ON;
IF OBJECT_ID(N'dbo.schema_versions', N'U') IS NULL
    CREATE TABLE dbo.schema_versions (
        version int NOT NULL CONSTRAINT PK_schema_versions PRIMARY KEY,
        applied_at datetime2 NOT NULL CONSTRAINT DF_schema_versions_applied DEFAULT SYSUTCDATETIME()
    );
IF EXISTS (SELECT 1 FROM dbo.schema_versions WHERE version > 1)
    THROW 51000, 'Database schema is newer than this application.', 1;
IF NOT EXISTS (SELECT 1 FROM dbo.schema_versions WHERE version = 1)
BEGIN
    CREATE TABLE dbo.orders (
        id varchar(50) COLLATE Latin1_General_100_BIN2 NOT NULL CONSTRAINT PK_orders PRIMARY KEY,
        mode varchar(10) COLLATE Latin1_General_100_BIN2 NOT NULL,
        access_key varchar(48) COLLATE Latin1_General_100_BIN2 NOT NULL,
        callback_key varchar(48) COLLATE Latin1_General_100_BIN2 NOT NULL,
        status varchar(20) COLLATE Latin1_General_100_BIN2 NOT NULL,
        amount int NOT NULL,
        currency varchar(3) COLLATE Latin1_General_100_BIN2 NOT NULL,
        items nvarchar(max) NOT NULL,
        link_id nvarchar(256) NULL,
        hpp_url nvarchar(max) NULL,
        transaction_id nvarchar(256) NULL,
        gp_status nvarchar(200) NULL,
        verified_response nvarchar(max) NULL,
        cancelled_at varchar(40) NULL,
        created_at varchar(40) NOT NULL,
        updated_at varchar(40) NOT NULL
    );
    CREATE TABLE dbo.payment_events (
        id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_payment_events PRIMARY KEY,
        order_id varchar(50) COLLATE Latin1_General_100_BIN2 NOT NULL,
        source varchar(80) NOT NULL,
        payload nvarchar(max) NOT NULL,
        verified int NOT NULL,
        created_at varchar(40) NOT NULL,
        CONSTRAINT FK_payment_events_orders FOREIGN KEY (order_id) REFERENCES dbo.orders(id)
    );
    CREATE INDEX IX_payment_events_order_id_id ON dbo.payment_events(order_id, id);
    INSERT INTO dbo.schema_versions(version) VALUES (1);
END;
