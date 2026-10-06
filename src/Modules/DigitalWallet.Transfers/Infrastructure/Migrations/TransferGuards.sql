CREATE FUNCTION transfers.guard_quote() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP IN ('DELETE', 'TRUNCATE') THEN RAISE EXCEPTION 'Quotes are retained' USING ERRCODE = '23514'; END IF;
    IF OLD."UsedAt" IS NOT NULL OR
       (to_jsonb(NEW) - ARRAY['UsedAt','Version','ConcurrencyVersion']) IS DISTINCT FROM
       (to_jsonb(OLD) - ARRAY['UsedAt','Version','ConcurrencyVersion']) OR
       NEW."UsedAt" IS NULL OR NEW."Version" <> OLD."Version" + 1 OR NEW."ConcurrencyVersion" = OLD."ConcurrencyVersion" THEN
        RAISE EXCEPTION 'Quote is immutable and single-use' USING ERRCODE = '23514';
    END IF;
    RETURN NEW;
END $$;
CREATE TRIGGER guard_quote BEFORE UPDATE OR DELETE ON transfers.quotes FOR EACH ROW EXECUTE FUNCTION transfers.guard_quote();
CREATE TRIGGER guard_quote_truncate BEFORE TRUNCATE ON transfers.quotes FOR EACH STATEMENT EXECUTE FUNCTION transfers.guard_quote();

CREATE FUNCTION transfers.guard_transfer() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP IN ('DELETE', 'TRUNCATE') THEN RAISE EXCEPTION 'Transfers are retained' USING ERRCODE = '23514'; END IF;
    IF OLD."Status" IN ('Completed','Rejected','Failed','PendingReview') OR
       (to_jsonb(NEW) - ARRAY['Status','ReasonCode','OtpChallengeId','OtpIssuedAt','ConfirmedAt','JournalId','CompletedAt','Version','ConcurrencyVersion']) IS DISTINCT FROM
       (to_jsonb(OLD) - ARRAY['Status','ReasonCode','OtpChallengeId','OtpIssuedAt','ConfirmedAt','JournalId','CompletedAt','Version','ConcurrencyVersion']) OR
       NEW."Version" <= OLD."Version" OR NEW."ConcurrencyVersion" = OLD."ConcurrencyVersion" OR
       (OLD."Status" = 'Validating' AND NEW."Status" = 'PendingConfirmation') OR
       (OLD."ConfirmedAt" IS NOT NULL AND (NEW."ConfirmedAt" IS DISTINCT FROM OLD."ConfirmedAt" OR NEW."OtpChallengeId" IS DISTINCT FROM OLD."OtpChallengeId")) THEN
        RAISE EXCEPTION 'Invalid transfer mutation' USING ERRCODE = '23514';
    END IF;
    RETURN NEW;
END $$;
CREATE TRIGGER guard_transfer BEFORE UPDATE OR DELETE ON transfers.transfers FOR EACH ROW EXECUTE FUNCTION transfers.guard_transfer();
CREATE TRIGGER guard_transfer_truncate BEFORE TRUNCATE ON transfers.transfers FOR EACH STATEMENT EXECUTE FUNCTION transfers.guard_transfer();

CREATE FUNCTION transfers.guard_idempotency() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP IN ('DELETE', 'TRUNCATE') THEN RAISE EXCEPTION 'Idempotency records are retained' USING ERRCODE = '23514'; END IF;
    IF (to_jsonb(NEW) - 'ResponseJson') IS DISTINCT FROM (to_jsonb(OLD) - 'ResponseJson') THEN
        RAISE EXCEPTION 'Idempotency identity is immutable' USING ERRCODE = '23514';
    END IF;
    RETURN NEW;
END $$;
CREATE TRIGGER guard_idempotency BEFORE UPDATE OR DELETE ON transfers.idempotency FOR EACH ROW EXECUTE FUNCTION transfers.guard_idempotency();
CREATE TRIGGER guard_idempotency_truncate BEFORE TRUNCATE ON transfers.idempotency FOR EACH STATEMENT EXECUTE FUNCTION transfers.guard_idempotency();

-- Local schema integrity only. Cross-module consistency is owned by the one shared transaction.
CREATE FUNCTION transfers.validate_completion() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE tid uuid; t transfers.transfers; q transfers.quotes; r transfers.idempotency;
BEGIN
    IF TG_TABLE_NAME = 'quotes' THEN SELECT "Id" INTO tid FROM transfers.transfers WHERE "QuoteId" = NEW."Id";
    ELSIF TG_TABLE_NAME = 'idempotency' THEN tid := NEW."TransferId";
    ELSE tid := NEW."Id"; END IF;
    IF tid IS NULL THEN
        IF TG_TABLE_NAME = 'quotes' AND NEW."UsedAt" IS NOT NULL THEN RAISE EXCEPTION 'Used quote needs a transfer' USING ERRCODE = '23514'; END IF;
        RETURN NULL;
    END IF;
    SELECT * INTO STRICT t FROM transfers.transfers WHERE "Id" = tid;
    SELECT * INTO STRICT q FROM transfers.quotes WHERE "Id" = t."QuoteId";
    SELECT * INTO r FROM transfers.idempotency WHERE "TransferId" = tid;
    IF t."Status" = 'Validating' OR r."Id" IS NULL OR r."OwnerCustomerId" <> t."OwnerCustomerId" OR
       (r."ResponseJson"::jsonb ->> 'Id') IS DISTINCT FROM t."Id"::text OR
       (r."ResponseJson"::jsonb ->> 'Status') IS DISTINCT FROM t."Status" OR
       t."OwnerCustomerId" <> q."OwnerCustomerId" OR t."SenderWalletId" <> q."SenderWalletId" OR t."TargetWalletId" <> q."TargetWalletId" OR
       t."TargetCustomerId" <> q."TargetCustomerId" OR t."BeneficiaryId" <> q."BeneficiaryId" OR
       t."AmountMinor" <> q."AmountMinor" OR t."FeeMinor" <> q."FeeMinor" OR t."Currency" <> q."Currency" OR
       t."AssessmentJson" <> q."AssessmentJson" OR t."ExpiresAt" <> q."ExpiresAt" OR
       (t."Status" = 'Completed' AND q."UsedAt" IS DISTINCT FROM t."CompletedAt") OR
       (t."Status" <> 'Completed' AND q."UsedAt" IS NOT NULL) THEN
        RAISE EXCEPTION 'Transfer, quote and receipt must agree at commit' USING ERRCODE = '23514';
    END IF;
    RETURN NULL;
END $$;
CREATE CONSTRAINT TRIGGER validate_transfer AFTER INSERT OR UPDATE ON transfers.transfers DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION transfers.validate_completion();
CREATE CONSTRAINT TRIGGER validate_quote AFTER INSERT OR UPDATE ON transfers.quotes DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION transfers.validate_completion();
CREATE CONSTRAINT TRIGGER validate_receipt AFTER INSERT OR UPDATE ON transfers.idempotency DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION transfers.validate_completion();
