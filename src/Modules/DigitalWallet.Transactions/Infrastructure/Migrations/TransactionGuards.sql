CREATE FUNCTION transactions.retain_fact() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN RAISE EXCEPTION 'Financial history is immutable' USING ERRCODE = '23514'; END $$;
CREATE TRIGGER retain_transaction BEFORE UPDATE OR DELETE ON transactions.transactions FOR EACH ROW EXECUTE FUNCTION transactions.retain_fact();
CREATE TRIGGER retain_transactions BEFORE TRUNCATE ON transactions.transactions FOR EACH STATEMENT EXECUTE FUNCTION transactions.retain_fact();

CREATE FUNCTION transactions.guard_request() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP IN ('DELETE','TRUNCATE') THEN RAISE EXCEPTION 'Reversal requests are retained' USING ERRCODE = '23514'; END IF;
    IF TG_OP = 'INSERT' THEN
        IF NEW."Status" <> 'Pending' THEN RAISE EXCEPTION 'Reversal must start pending' USING ERRCODE = '23514'; END IF;
    ELSE
        IF OLD."Status" <> 'Pending' OR NEW."Status" NOT IN ('Completed','Rejected') OR
            (to_jsonb(NEW) - ARRAY['Status','DecidedBy','DecidedAt','DecisionNote','ApprovalKey','ApprovalHash','OtpChallengeId','ReversalTransactionId']) IS DISTINCT FROM
            (to_jsonb(OLD) - ARRAY['Status','DecidedBy','DecidedAt','DecisionNote','ApprovalKey','ApprovalHash','OtpChallengeId','ReversalTransactionId']) THEN
            RAISE EXCEPTION 'Invalid reversal transition' USING ERRCODE = '23514';
        END IF;
    END IF;
    RETURN NEW;
END $$;
CREATE TRIGGER guard_request BEFORE INSERT OR UPDATE OR DELETE ON transactions.reversal_requests FOR EACH ROW EXECUTE FUNCTION transactions.guard_request();
CREATE TRIGGER retain_requests BEFORE TRUNCATE ON transactions.reversal_requests FOR EACH STATEMENT EXECUTE FUNCTION transactions.guard_request();

CREATE FUNCTION transactions.check_reversal() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE r transactions.transactions; o transactions.transactions; q transactions.reversal_requests;
BEGIN
    IF TG_TABLE_NAME = 'reversal_requests' THEN
        SELECT * INTO q FROM transactions.reversal_requests WHERE "Id" = NEW."Id";
        SELECT * INTO o FROM transactions.transactions WHERE "Id" = q."OriginalTransactionId";
        IF o."Type" IS DISTINCT FROM 'InternalTransfer' THEN RAISE EXCEPTION 'Only transfers are reversible' USING ERRCODE = '23514'; END IF;
        IF q."Status" <> 'Completed' THEN RETURN NULL; END IF;
        SELECT * INTO r FROM transactions.transactions WHERE "Id" = q."ReversalTransactionId";
    ELSE
        IF NEW."Type" <> 'Reversal' THEN RETURN NULL; END IF;
        r := NEW;
        SELECT * INTO q FROM transactions.reversal_requests WHERE "Id" = r."Id";
        SELECT * INTO o FROM transactions.transactions WHERE "Id" = r."ReversalOf";
    END IF;
    IF q."Status" IS DISTINCT FROM 'Completed' OR r."Type" IS DISTINCT FROM 'Reversal' OR o."Type" IS DISTINCT FROM 'InternalTransfer' OR
        q."OriginalTransactionId" IS DISTINCT FROM o."Id" OR q."ReversalTransactionId" IS DISTINCT FROM r."Id" OR
        r."ReversalOf" IS DISTINCT FROM o."Id" OR r."TransferId" IS DISTINCT FROM o."TransferId" OR
        r."SenderCustomerId" IS DISTINCT FROM o."RecipientCustomerId" OR r."RecipientCustomerId" IS DISTINCT FROM o."SenderCustomerId" OR
        r."SenderWalletId" IS DISTINCT FROM o."RecipientWalletId" OR r."RecipientWalletId" IS DISTINCT FROM o."SenderWalletId" OR
        r."AmountMinor" IS DISTINCT FROM o."AmountMinor" OR r."FeeMinor" IS DISTINCT FROM o."FeeMinor" OR r."Currency" IS DISTINCT FROM o."Currency" OR
        r."MaskedSenderName" IS DISTINCT FROM o."MaskedRecipientName" OR r."MaskedRecipientName" IS DISTINCT FROM o."MaskedSenderName" OR
        r."OccurredAt" < o."OccurredAt" OR
        r."SenderDebitMinor" IS DISTINCT FROM o."RecipientCreditMinor" OR r."RecipientCreditMinor" IS DISTINCT FROM o."SenderDebitMinor" THEN
        RAISE EXCEPTION 'Reversal fact and decision must match' USING ERRCODE = '23514';
    END IF;
    RETURN NULL;
END $$;
CREATE CONSTRAINT TRIGGER check_reversal_fact AFTER INSERT ON transactions.transactions DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION transactions.check_reversal();
CREATE CONSTRAINT TRIGGER check_reversal_request AFTER INSERT OR UPDATE ON transactions.reversal_requests DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION transactions.check_reversal();
