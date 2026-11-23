module Arkham.Native.Settings.Predicate
  (Truth(..),negateTruth,allTruth,anyTruth,Fact(..),CrashDraft(..)
  ,PredicateState,predicateFacts,predicateCrash,emptyPredicateState,makePredicateState
  ,definitionFactDomain,evaluateCondition,conditionAtoms,readsPriorState) where

import Arkham.Prelude
import Arkham.Native.Settings.Definition

data Truth = ProvenTrue | ProvenFalse | Unanswered
  deriving stock (Show, Eq, Ord)

negateTruth :: Truth -> Truth
negateTruth ProvenTrue = ProvenFalse
negateTruth ProvenFalse = ProvenTrue
negateTruth Unanswered = Unanswered

allTruth :: [Truth] -> Truth
allTruth values
  | ProvenFalse `elem` values = ProvenFalse
  | all (== ProvenTrue) values = ProvenTrue
  | otherwise = Unanswered

anyTruth :: [Truth] -> Truth
anyTruth = negateTruth . allTruth . map negateTruth

data Fact
  = KeyFact Text (Maybe Text)
  | OptionFact Text
  | RecordFact Bool Text RecordKind Value
  | CountFact Text (Maybe Int)
  deriving stock (Show, Eq)

-- Explicit crash states are not a generic False/Unknown conversion. These
-- semantics match the reviewed editor: until a victim is chosen, every
-- partner remains available, but the required victim itself is unfinished.
data CrashDraft = CrashAbsent | CrashUnanswered | CrashChosen Text
  deriving stock (Show, Eq)

data PredicateState = PredicateState
  { predicateFacts :: [(Fact,Truth)]
  , predicateCrash :: CrashDraft
  }
  deriving stock (Show, Eq)

emptyPredicateState :: PredicateState
emptyPredicateState = PredicateState [] CrashAbsent

-- The closed domain must be derived from the resolved context, not just the
-- submitted draft. Missing legal producers therefore become Unanswered.
-- Foreign/future facts cannot be introduced, and counts have ONE current
-- effective writer after the interpreter's explicit chapter merge policy.
makePredicateState :: [Fact] -> [(Fact,Truth)] -> CrashDraft -> Either Text PredicateState
makePredicateState domain supplied crash = do
  let normalized = nub $ map factIdentity domain
      counts = [key | (CountFact key _,_) <- supplied]
  unless (length counts == length (nub counts)) $ Left "Conflicting settings count facts"
  unless (all (\(fact,_) -> factIdentity fact `elem` normalized) supplied) $ Left "Settings fact outside resolved context"
  let facts = concatMap (\fact -> case filter ((== fact) . factIdentity . fst) supplied of
        [] -> [(fact,Unanswered)]
        matches -> matches) normalized
  pure $ PredicateState facts crash

factIdentity :: Fact -> Fact
factIdentity (CountFact key _) = CountFact key Nothing
factIdentity fact = fact

definitionFactDomain :: Maybe Text -> SettingRow -> [Fact]
definitionFactDomain scope row = case rowEffect row of
  KeyEffect key _ -> [KeyFact key scope]
  SelectedKeyEffect -> [KeyFact key scope | choice <- rowChoices row,String key <- [choiceValue choice]]
  OptionEffect key _ -> [OptionFact key]
  LocalChoiceEffect -> [OptionFact key | choice <- rowChoices row,Just key <- [choiceConditionKey choice]]
  CountEffect key _ -> [CountFact key Nothing]
  RecordEffect key _ kind fixed -> [RecordFact crossed key kind value
    | value <- maybe (map choiceValue $ rowChoices row) pure fixed,crossed <- [False,True]]
  SelectedRecordTarget kind value -> [RecordFact False key kind value
    | choice <- rowChoices row,String key <- [choiceValue choice]]
  _ -> []

evaluateCondition :: PredicateState -> Condition -> Truth
evaluateCondition state = go
 where
  facts = predicateFacts state
  matching predicate = anyTruth [truth | (fact,truth) <- facts,predicate fact]
  go = \case
    Always -> ProvenTrue
    AllOf children -> allTruth $ map go children
    AnyOf children -> anyTruth $ map go children
    NoneOf children -> negateTruth $ anyTruth $ map go children
    Not condition -> negateTruth $ go condition
    HasKey key scope -> matching $ \case
      KeyFact actual actualScope -> actual == key && maybe True (\s -> actualScope == Just s) scope
      _ -> False
    HasOption option -> matching (== OptionFact option)
    HasRecord crossed key kind value -> matching $ \case
      RecordFact actualCrossed actual actualKind actualValue -> actualCrossed == crossed && actual == key
        && maybe True (== actualKind) kind && actualValue == value
      _ -> False
    CountAtLeast key n -> countMatches key (>= n)
    CountAtMost key n -> countMatches key (<= n)
    NotCrashVictim victim -> case predicateCrash state of
      CrashChosen actual | actual == victim -> ProvenFalse
      _ -> ProvenTrue
  countMatches key predicate = case
    [ case (value,truth) of
        (_,Unanswered) -> Unanswered
        (_,ProvenFalse) -> ProvenFalse
        (Nothing,_) -> Unanswered
        (Just n,_) -> if predicate n then ProvenTrue else ProvenFalse
    | (CountFact actual value,truth) <- facts,actual == key ] of
      [] -> ProvenFalse
      [result] -> result
      _ -> Unanswered -- defensive; smart constructor rejects duplicate counts

conditionAtoms :: Condition -> [Condition]
conditionAtoms = \case
  AllOf children -> concatMap conditionAtoms children
  AnyOf children -> concatMap conditionAtoms children
  NoneOf children -> concatMap conditionAtoms children
  Not child -> conditionAtoms child
  Always -> []
  atom -> [atom]

-- This is the one reviewed self-reference. It reads facts BEFORE this row,
-- not its own proposed output. Do not generalize this exception to all rows
-- or suppress actual cycles such as TCU's four mutually exclusive fates.
readsPriorState :: Text -> Condition -> Bool
readsPriorState ident atom = ident == "campaign:02/step/5/2" && atom == HasKey "TheNecronomiconWasStolen" Nothing
