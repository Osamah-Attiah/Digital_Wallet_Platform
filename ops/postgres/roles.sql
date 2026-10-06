\getenv api_password api_password
\getenv worker_password worker_password
\getenv migrator_password migrator_password
CREATE ROLE wallet_migrator LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION PASSWORD :'migrator_password';
CREATE ROLE wallet_api LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION PASSWORD :'api_password';
CREATE ROLE wallet_worker LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION PASSWORD :'worker_password';
ALTER DATABASE digital_wallet OWNER TO wallet_migrator;
REVOKE ALL ON DATABASE digital_wallet FROM PUBLIC;
GRANT CONNECT ON DATABASE digital_wallet TO wallet_migrator, wallet_api, wallet_worker;
REVOKE ALL ON SCHEMA public FROM PUBLIC;
