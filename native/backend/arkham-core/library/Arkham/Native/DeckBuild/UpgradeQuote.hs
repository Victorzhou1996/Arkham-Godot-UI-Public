{-# LANGUAGE NoImplicitPrelude #-}
{-# LANGUAGE OverloadedStrings #-}
{-# LANGUAGE TupleSections #-}
module Arkham.Native.DeckBuild.UpgradeQuote where
import Prelude
import Control.Monad (unless,foldM)
import Data.Aeson
import Data.Aeson.Types (parseEither,Parser)
import Data.Aeson.Key qualified as K
import Data.Aeson.KeyMap qualified as KM
import Data.Map.Strict (Map)
import Data.Map.Strict qualified as Map
import Data.Maybe (fromMaybe)
import Data.List (find,sortBy,nub)
import Data.Set qualified as Set
import Data.Text (Text)
import Data.Text qualified as T
import Arkham.Card.CardCode (CardCode(..))
import Arkham.Native.DeckMetadata
import Arkham.Native.DeckBuild.Catalog
import Arkham.Native.DeckBuild.Types
import Arkham.Native.DeckBuild.Taboos qualified as Taboo
import Arkham.Native.DeckBuild.Investigators qualified as Inv
import Arkham.Native.DeckBuild.Customizations qualified as Custom
import Arkham.Native.DeckBuild.Selections qualified as Selection
import Arkham.Native.DeckBuild.Extra qualified as Extra

decodeQ :: FromJSON a => Value -> Either Text a
decodeQ = either (Left . T.pack) Right . parseEither parseJSON
raw :: Text -> CatalogCard -> Value
raw = Inv.rawField
number :: Text -> CatalogCard -> Int
number key c = either (const 0) id $ decodeQ $ raw key c
flag :: Text -> CatalogCard -> Bool
flag key c = raw key c==Bool True
printed :: CatalogCard -> Maybe Int
printed c = either (const Nothing) id $ decodeQ $ raw "xp" c
-- Original shared/card-utils.ts: exceptional BEFORE chained; customization not per copy.
experience :: CatalogCard -> Int -> Int
experience c quantity
  | number "customization_xp" c>0 = number "customization_xp" c
  | otherwise = (fromMaybe 0 (printed c)*(if flag "exceptional" c then 2 else 1)+number "taboo_xp" c)
      *(if flag "myriad" c then min quantity 1 else quantity)
realLevel :: CatalogCard -> Maybe Int
realLevel c = (+number "taboo_xp" c) <$> cardLevel c
data Version = Version DeckInput Catalog (Map Text Int) (Map Text (Map Int Int)) [Issue]

prepare :: Catalog -> Value -> Either Text Version
prepare base value = do
  deck<-decodeQ value
  meta<-Selection.decodeDeckMeta $ deckMeta deck
  cat<-Taboo.applyTabooCatalog (deckTaboo deck) base
  let side key=case KM.lookup key meta of Nothing->Right Nothing;Just Null->Right Nothing;Just v->Just <$> decodeQ v
  front0<-side "alternate_front";back0<-side "alternate_back"
  let clean = fmap normalizeCode . (>>= (\x -> if T.null x then Nothing else Just x))
      request=BuildRequest (deckInvestigator deck) "upgrade" (deckTaboo deck) (clean front0) (clean back0) (object [])
  Inv.validateSideMeta request meta
  sides<-Inv.resolveSides cat request
  extra<-Extra.deriveExtraPlan cat (Inv.backCard sides) meta
  projection<-Custom.projectSelectedCustomizations cat meta $ Map.unionsWith (+) [deckSlots deck,deckSideSlots deck,Extra.extraQuantities extra]
  let projected=Custom.projectedCatalog projection
      allCodes=Map.keys (deckSlots deck)<>Map.keys (Extra.extraQuantities extra)
  mapM_ (Inv.lookupCard projected) (allCodes<>Map.keys (deckSideSlots deck))
  marks<-traverse (\(key,v)->do
      text<-decodeQ v
      let code=T.drop 4 $ K.toText key
      parsed<-validatedCustomizations (CardCode code) text
      pure (code,Map.fromList [(customizationIndex x,customizationMarks x)|x<-parsed]))
    [(k,v)|(k,v)<-KM.toList meta,"cus_" `T.isPrefixOf` K.toText k]
  let unknown=[Issue "quoteDeckFieldUnsupported" x | x<-deckUnknownFields deck,x `notElem` ["xp","xp_adjustment","exile_string","ignoreDeckLimitSlots"]]
      variants=[Issue "quoteVariantUnsupported" c |(c,v)<-Map.toList $ deckVariants deck,not(Taboo.variantAllowed (deckTaboo deck) c v)]
      unknownMeta=[Issue "quoteMetadataPending" key | k<-KM.keys meta,let key=K.toText k
        ,not(any (`T.isPrefixOf` key) ["cus_","annotation_"]),key `notElem` ["alternate_front","alternate_back","extra_deck","deck_size_selected","faction_selected","faction_1","faction_2","option_selected","attachments_03264","attachments_05002","attachments_07303","attachments_09077","attachments_10079","attachments_11080","arkham_horror_description_md"]]
  pure $ Version deck projected (Extra.extraQuantities extra) (Map.fromList marks)
    (unknown<>variants<>unknownMeta<>Extra.extraIssues extra<>Custom.customizationIssues projection)

type Quantities = Map Text Int
data Pools = Pools { adaptable :: Int, research :: Int, deja :: Int, rabbit :: Int
                   , researchCards :: Int, rabbitCards :: Int } deriving (Show,Eq)
data QuoteRun = QuoteRun { runCost :: Int, runPools :: Pools, runItems :: [Value], runIssues :: [Issue] } deriving Show
qty :: Text -> Quantities -> Int
qty = Map.findWithDefault 0
traitsText :: CatalogCard -> Text
traitsText c=case raw "traits" c of String t->t;_->""
spell :: CatalogCard -> Bool
spell c="Spell" `elem` map T.strip (T.splitOn "." $ traitsText c)
canUpgrade :: CatalogCard -> CatalogCard -> Bool
canUpgrade previous next=cardName previous==cardName next && fromMaybe 0(printed previous)<fromMaybe 0(printed next)
subtitle :: CatalogCard -> Value
subtitle c=case raw "subname" c of Null->raw "real_subname" c;v->v
canSwap :: CatalogCard -> CatalogCard -> Bool
canSwap previous next=cardName previous==cardName next && printed previous==printed next && subtitle previous==subtitle next
subtitleUncertain :: CatalogCard -> CatalogCard -> Bool
subtitleUncertain a b=cardCode a/=cardCode b && cardName a==cardName b && printed a==printed b
  && (subtitle a `notElem` [Null,String ""] || subtitle b `notElem` [Null,String ""])
  && (raw "subname" a==Null || raw "subname" b==Null)
initialPools :: Quantities -> Quantities -> Pools
initialPools old new=Pools (qty "02110" new*2) ar (qty "60531" new*3) dr ar dr
 where ar=qty "04109" old;dr=qty "08059" old*2
applyRabbit :: Int -> Pools -> (Int,Pools)
applyRabbit cost p
  | rabbitCards p<=0=(cost,p)
  | otherwise=(cost-d,p {rabbit=rabbit p-d,rabbitCards=rabbitCards p-1})
 where d=if cost>0 then 1 else 0
applyResearch :: Int -> Pools -> (Int,Pools)
applyResearch cost p
  | researchCards p<=0=(cost,p)
  | otherwise=(cost-d,p {research=research p-d,researchCards=0})
 where d=min (research p) (max 0 cost)
applyUpgradeDiscount :: Bool -> Bool -> Int -> Pools -> (Int,Pools)
applyUpgradeDiscount rabbitFirst isSpell cost p =
  let ar=if isSpell then applyResearch else \n s->(n,s)
      (c1,p1)=(if rabbitFirst then applyRabbit else ar) cost p
  in (if rabbitFirst then ar else applyRabbit) c1 p1
applyDeja :: Int -> Int -> Int -> Pools -> (Int,Pools)
applyDeja copies owned cost p=foldl step (cost,p) [1..copies]
 where step (c,s) _ | deja s>0 && c>0=let d=min owned c in (c-d,s {deja=deja s-d})
                    | otherwise=(c,s)
stats :: Pools -> Pools -> Value
stats before after=object [K.fromText key .= object ["available" .= n,"used" .= (n-left)]
  |(key,n,left)<-[("adaptable",adaptable before,adaptable after),("arcaneResearch",research before,research after)
                ,("dejaVu",deja before,deja after),("downTheRabbitHole",rabbit before,rabbit after)],n>0]

type Diff = ([(CatalogCard,Int)],[(CatalogCard,Int)])
makeDiff :: Catalog -> Catalog -> Quantities -> Quantities -> Quantities -> Either Text Diff
makeDiff oldCat newCat old new exiled=do
  let changes=[(c,qty c new-qty c old+qty c exiled)|c<-Map.keys $ Map.union old new]
  adds<-traverse (\(c,n)->(,n) <$> Inv.lookupCard newCat c) $ filter ((>0) . snd) changes
  removes<-traverse (\(c,n)->(,abs n) <$> Inv.lookupCard oldCat c) $ filter ((<0) . snd) changes
  pure (sortBy (compareAdds removes) adds,removes)
compareAdds :: [(CatalogCard,Int)] -> (CatalogCard,Int) -> (CatalogCard,Int) -> Ordering
compareAdds removes (a,_) (b,_)=case (find (\(x,_)->canUpgrade x a) removes,find (\(x,_)->canUpgrade x b) removes) of
  (Just(x,_),Just(y,_))->let bySpell=compare ("Spell." `T.isInfixOf` traitsText a) ("Spell." `T.isInfixOf` traitsText b)
                        in if bySpell/=EQ then bySpell else compare (level b-fromMaybe 0(realLevel y)) (level a-fromMaybe 0(realLevel x))
  _->compare (level b) (level a)
 where level=fromMaybe (-1) . realLevel

-- Swaps deliberately do NOT consume removals; original getSwaps is independent.
-- Only getDirectUpgrades consumes an expanded list of removed instances.
directUpgrades :: Diff -> Map Text [CatalogCard]
directUpgrades (adds,removes)=snd $ foldl step (concat [replicate n c|(c,n)<-removes],Map.empty) adds
 where
  step (left,result) (c,n)=let (used,rest)=pick n c left in (rest,Map.insert (cardCode c) used result)
  pick 0 _ xs=([],xs)
  pick n c xs=case break (`canUpgrade` c) xs of
    (_,[])->([],xs)
    (before,x:after)->let (more,left)=pick (n-1) c (before<>after) in (x:more,left)

canonicalName :: CatalogCard -> Text
canonicalName c | cardCode c `elem` ["08114","08115"]=cardName c<>" ("<>(case raw "real_subname" c of String t->t;_->"")<>")"
                | otherwise=cardName c
limitsByName :: Catalog -> Quantities -> Either Text (Map Text (Int,Int))
limitsByName cat slots=Map.fromListWith (\(q,a)(r,b)->(q+r,min a b)) <$> traverse (\(code,n)->do
  c<-Inv.lookupCard cat code
  pure (canonicalName c,(n,fromMaybe 0 $ cardLimit c))) (Map.toList slots)
sizeAdjust :: CatalogCard -> Either Text Int
sizeAdjust c=case cardRequirements c of
  Nothing->Right 0
  Just(Object o)->case KM.lookup "size" o of Nothing->Right 0;Just Null->Right 0;Just v->decodeQ v
  _->Left "Unknown owned card requirements in upgrade quote"
freeSlots :: Bool -> Catalog -> Catalog -> Quantities -> Quantities -> Quantities -> Diff -> Pools -> Either Text Int
freeSlots mainZone oldCat newCat old new exile (adds,removes) pools=do
  oldLimits<-limitsByName oldCat old;newLimits<-limitsByName newCat new
  adjustment<-if not mainZone then Right 0 else sum <$> traverse (\(code,n)->do
    c<-Inv.lookupCard newCat code
    size<-sizeAdjust c
    let limit=fromMaybe 0(cardLimit c)
        compensate=case (Map.lookup (canonicalName c) oldLimits,Map.lookup (canonicalName c) newLimits) of
          (Just(oq,ol),Just(nq,nl)) | limit<=1 && nl<ol->max 0 (oq-nq)
          _->0
    pure (size*(n-qty code old)+compensate)) [(code,n)|(code,n)<-Map.toList new,n>qty code old]
  let permanentFill=sum [n|(c,n)<-adds,cardPermanent c,Just(prev,_)<-[find (\(x,_)->canUpgrade x c) removes],not(cardPermanent prev)]
  pure $ adaptable pools+max 0 adjustment+sum [n|(c,n)<-Map.toList exile,qty c old>0]+permanentFill

priceZone :: Bool -> Text -> Version -> Version -> Quantities -> Quantities -> Quantities -> Quantities -> Map Text (Map Int Int) -> QuoteRun -> Either Text QuoteRun
priceZone rabbitFirst zone (Version old oldCat _ _ _) (Version new newCat _ _ _) oldSlots newSlots exile ignored delta start=do
  diff@(adds,removes)<-makeDiff oldCat newCat oldSlots newSlots exile
  free<-freeSlots (zone=="main") oldCat newCat oldSlots newSlots exile diff (runPools start)
  ignoredCards<-traverse (Inv.lookupCard newCat) $ Map.keys ignored
  let upgrades=directUpgrades diff
      rabbitEnabled=qty "08059" (deckSlots old)>0
      ambiguities=[Issue "quoteSubtitleAmbiguous" (cardCode a<>":"<>cardCode b)|(a,_)<-adds,(b,_)<-removes,subtitleUncertain a b]
        <>[Issue "quoteUpgradePairingAmbiguous" (cardCode c)|(c,_)<-adds,length(nub [cardCode p|(p,_)<-removes,canUpgrade p c])>1]
        <>[Issue "quoteOriginalSortAmbiguous" "Original comparator gives an inconsistent ordering for this mixture"
          |or [compareAdds removes a b==GT |(i,a)<-zip [0::Int ..] adds,(j,b)<-zip [0::Int ..] adds,i<j]]
        <>[Issue "quoteTiedDiscountOrderAmbiguous" "Equal sort keys have different discount eligibility; original object order is unavailable"
          |rabbitEnabled || qty "04109" (deckSlots old)>0
          ,or [compareAdds removes a b==EQ && eligibility (fst a)/=eligibility (fst b)
               |(i,a)<-zip [0::Int ..] adds,(j,b)<-zip [0::Int ..] adds,i<j]]
      eligibility c=(not(null $ Map.findWithDefault [] (cardCode c) upgrades),spell c
        ,any ((==cardName c) . cardName) ignoredCards
        ,qty (cardCode c) (deckSlots old)>0 && Map.member (cardCode c) delta && qty (cardCode c) exile==0
        ,experience c 1,length $ Map.findWithDefault [] (cardCode c) upgrades
        ,maybe 0 snd $ find ((==cardCode c) . cardCode . fst) adds)
      step (result,left,seen) (c,n)=
        let code=cardCode c
            customChanged=qty code (deckSlots old)>0 && qty code exile==0 && any (/=0) (Map.elems $ Map.findWithDefault Map.empty code delta)
            swapCount=maybe 0 (min n . snd) $ find (\(x,_)->canSwap x c) removes
            quantity=n-swapCount-(if code=="05040" then qty code ignored else 0)
            level=realLevel c
            skipReason | customChanged="customization-separate" | quantity<=0="swap-or-story-grant" | level==Nothing="story" | flag "myriad" c && Set.member (cardName c) seen="myriad-already-counted" | otherwise="purchase"
            skip=skipReason/="purchase"
            initial=if skip then 0 else experience c quantity+(if level==Just 0 then if flag "myriad" c then 1 else quantity else 0)
            (afterDeja,p0)=if not skip && qty code exile>0 && maybe False (>0) level then applyDeja (min quantity $ qty code exile) (qty "60531" $ deckSlots new) initial (runPools result) else (initial,runPools result)
            isIgnored=any ((==cardName c) . cardName) ignoredCards
            from=Map.findWithDefault [] code upgrades
            mixed=not skip && not isIgnored && rabbitEnabled && not(null from) && length from<quantity
            upgradeStep (cost,p) previous =
              let spent=experience c 1-experience previous 1
                  (discounted,p')=applyUpgradeDiscount rabbitFirst (spell c) spent p
              in (cost-experience previous 1-(spent-discounted),p')
            (charged,p1,left1)
              | skip=(0,runPools result,left)
              | isIgnored=(afterDeja+if rabbitEnabled then quantity else 0,p0,left)
              | not(null from)=let (x,p)=foldl upgradeStep (afterDeja,p0) from in (x,p,left)
              | level==Just 0=let used=min (max 0 afterDeja) (min left quantity) in (afterDeja-used,p0,left-used)
              | rabbitEnabled && quantity-qty code exile>0=(afterDeja+if flag "myriad" c then 1 else quantity-qty code exile,p0,left)
              | otherwise=(afterDeja,p0,left)
            row=object ["zone" .= zone,"cardCode" .= code,"added" .= n,"swapped" .= swapCount,"upgraded" .= length from
              ,"reason" .= (skipReason::Text),"beforeDiscounts" .= initial,"cost" .= charged
              ,"freeFillUsed" .= (left-left1),"modifiers" .= stats (runPools result) p1]
            issues'=runIssues result<>[Issue "quoteMixedRabbitPurchase" code|mixed]
        in (result {runCost=runCost result+charged,runPools=p1,runItems=runItems result<>[row],runIssues=issues'},left1
           ,if not skip && flag "myriad" c then Set.insert (cardName c) seen else seen)
      (end,left,_)=foldl step (start {runIssues=runIssues start<>ambiguities},free,Set.empty) adds
  pure end {runPools=(runPools end) {adaptable=min (adaptable $ runPools end) left}}

calculate :: Bool -> Version -> Version -> Quantities -> Quantities -> Map Text (Map Int Int) -> Either Text QuoteRun
calculate first oldV@(Version old oldCat oldExtra _ _) newV@(Version new _ newExtra _ _) exile ignored delta=do
  let p=initialPools (deckSlots old) (deckSlots new)
  mainResult<-priceZone first "main" oldV newV (deckSlots old) (deckSlots new) exile ignored delta $ QuoteRun 0 p [] []
  extraResult<-priceZone first "extra" oldV newV oldExtra newExtra exile ignored delta mainResult
  foldM (\r (code,entries)->if qty code (deckSlots old)==0 || qty code exile>0 then pure r else do
    c<-Inv.lookupCard oldCat code
    let baseCost=sum $ Map.elems entries
        (charged,p')=applyUpgradeDiscount first (spell c) baseCost (runPools r)
    pure r {runCost=runCost r+charged,runPools=p',runItems=runItems r<>
      [object ["zone" .= ("customizations"::Text),"cardCode" .= code,"beforeDiscounts" .= baseCost,"cost" .= charged,"modifiers" .= stats (runPools r) p']]})
    extraResult (Map.toList delta)

data Context = Context (Maybe Int) (Maybe Int) (Maybe Text) (Maybe Quantities) Bool
parseContext :: Value -> Either Text Context
parseContext = either (Left . T.pack) Right . parseEither (withObject "Upgrade context" $ \o->do
  exactFields ["availableXp","xpAdjustment","exileString","ignoredSlots","contextComplete"] o
  xp<-o .:? "availableXp";adj<-o .:? "xpAdjustment";exile<-o .:? "exileString";ignored<-o .:? "ignoredSlots"
  complete<-o .: "contextComplete"
  unless (maybe True (\x->x>=0 && x<=1000000) xp && maybe True (\x->x>=(-1000000) && x<=1000000) adj) $ fail "Invalid XP context bounds"
  unless (maybe True (\m->Map.size m<=200 && all (\n->n>=0 && n<=100) (Map.elems m) && sum(Map.elems m)<=500) ignored) $ fail "Invalid ignored quantities"
  pure $ Context xp adj exile ignored complete)

quoteUpgrade :: Catalog -> Value -> Either Text Value
quoteUpgrade base input=do
  (previous,proposed,contextValue)<-either (Left . T.pack) Right $ parseEither (withObject "Upgrade quote" $ \o->do
    exactFields ["previousDeck","proposedDeck","context"] o
    (,,) <$> o .: "previousDeck" <*> o .: "proposedDeck" <*> o .: "context") input
  Context xp adj exileText ignoredMaybe complete<-parseContext contextValue
  exileCodes<-maybe (Right []) (Extra.decodeExtraDeck . String) exileText
  let exile=Map.fromListWith (+) [(c,1::Int)|c<-exileCodes]
      ignored=Map.filter (>0) $ fromMaybe Map.empty ignoredMaybe
  oldV@(Version old _ oldExtra oldMarks oldIssues)<-prepare base previous
  newV@(Version new _ newExtra newMarks newIssues)<-prepare base proposed
  unless(deckInvestigator old==deckInvestigator new) $ Left "Replacement is not a deck upgrade quote"
  mapM_ (Inv.lookupCard base) (Map.keys exile<>maybe [] Map.keys ignoredMaybe)
  unless(all (\(c,n)->n<=qty c (deckSlots old)+qty c oldExtra) $ Map.toList exile) $ Left "Exile count exceeds previous owned copies"
  unless(all (\(c,n)->n<=qty c (deckSlots new)) $ Map.toList ignored) $ Left "Ignored quantity exceeds proposed main copies"
  case proposed of
    Object o->mapM_ (\(key,value)->case KM.lookup key o of Nothing->Right ();Just v->unless(v==value) $ Left "Proposed annotation conflicts with explicit quote context")
      [("xp",toJSON xp),("xp_adjustment",toJSON adj),("exile_string",toJSON exileText),("ignoreDeckLimitSlots",toJSON ignoredMaybe)]
    _->Left "Expected proposed deck"
  let differences c marks =
        let entries=Map.filterWithKey (\idx n->qty c exile>0 || Map.lookup idx (Map.findWithDefault Map.empty c oldMarks)/=Just n) marks
            ds=Map.mapWithKey (\idx n->n-(if qty c exile>0 then 0 else Map.findWithDefault 0 idx (Map.findWithDefault Map.empty c oldMarks))) entries
        in if Map.null ds then Nothing else Just ds
      delta=Map.mapMaybeWithKey differences newMarks
      decreased=or [qty c exile==0 && Map.findWithDefault 0 idx (Map.findWithDefault Map.empty c newMarks)<n|(c,marks)<-Map.toList oldMarks,(idx,n)<-Map.toList marks]
      common=oldIssues<>newIssues
        <>[Issue "quoteContextMissing" "Complete explicit XP, adjustment, exile and ignored-card provenance is required"|not complete || xp==Nothing || adj==Nothing || exileText==Nothing || ignoredMaybe==Nothing]
        <>[Issue "quoteExileZoneAmbiguous" c|c<-Map.keys exile,(qty c (deckSlots old)>0 || qty c (deckSlots new)>0) && (qty c oldExtra>0 || qty c newExtra>0)]
        <>[Issue "quoteCustomizationDecrease" "Customization removal/decrease cannot authorize a refund"|decreased]
  first<-calculate True oldV newV exile ignored delta
  second<-if qty "04109" (deckSlots old)>0 && qty "08059" (deckSlots old)>0 then calculate False oldV newV exile ignored delta else pure first
  let chosen=if runCost second<runCost first then second else first
      pending=nub $ common<>runIssues first<>runIssues second<>[Issue "quoteNegativeCost" "Negative calculated total is not a certified refund"|runCost chosen<0]
      supported=null pending
      finalCost=if supported then Just(runCost chosen) else Nothing
      remaining=(\x a c->x+a-c) <$> xp <*> adj <*> finalCost
  pure $ object ["policyId" .= ("original-build-upgrade-r2"::Text),"catalogDigest" .= catalogDigest,"tabooDigest" .= Taboo.tabooDigest
    ,"supported" .= supported,"issues" .= pending,"contextComplete" .= (complete && xp/=Nothing && adj/=Nothing && exileText/=Nothing && ignoredMaybe/=Nothing)
    ,"changes" .= runItems chosen,"calculatedCost" .= runCost chosen,"purchaseCost" .= finalCost
    ,"modifierStats" .= stats (initialPools (deckSlots old) (deckSlots new)) (runPools chosen)
    ,"discountOrder" .= (if runCost second<runCost first then "research-first" else "rabbit-first"::Text)
    ,"availableXp" .= xp,"xpAdjustment" .= adj,"remainingXp" .= remaining,"affordable" .= ((>=0) <$> remaining)
    ,"validatesDeckLegality" .= False,"authorizesGameMutation" .= False]


